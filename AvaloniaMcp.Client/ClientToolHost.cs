using System.Reflection;
using System.Text.Json;
using AvaloniaMcp.Core;
using AvaloniaMcp.Protocol;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaMcp.Client;

/// <summary>
/// Mechanic #1 — the in-app tool engine. Discovers the Core <c>[McpServerTool]</c>
/// methods, builds the SDK's <see cref="McpServerTool"/> objects bound to a DI
/// <see cref="IServiceProvider"/> that supplies the Avalonia spine, exposes their
/// protocol schemas as <see cref="ToolDescriptor"/>s for the hub catalog, and
/// invokes one by name with JSON arguments — returning MCP content the client can
/// serialize back to the hub over the pipe.
/// </summary>
/// <remarks>
/// The hub never sees Core/Avalonia: the client reports each tool's
/// name/description/input-schema (read straight off <see cref="McpServerTool.ProtocolTool"/>),
/// and the hub re-advertises those schemas verbatim. Invocation runs in-process here,
/// where the UI thread and the Core services live.
/// </remarks>
public sealed class ClientToolHost : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, McpServerTool> _tools;
    private readonly HashSet<string> _readOnlyTools;

    private ClientToolHost(IServiceProvider services, Dictionary<string, McpServerTool> tools, HashSet<string> readOnlyTools)
    {
        _services = services;
        _tools = tools;
        _readOnlyTools = readOnlyTools;
    }

    /// <summary>The tools, keyed by protocol name.</summary>
    public IReadOnlyDictionary<string, McpServerTool> Tools => _tools;

    /// <summary>
    /// The protocol names of the tools that only read UI state and never mutate it.
    /// A read-only client allows exactly these and refuses everything else. Derived
    /// per-tool by <see cref="IsReadOnly(McpServerTool)"/> at build time.
    /// </summary>
    public IReadOnlyCollection<string> ReadOnlyToolNames => _readOnlyTools;

    /// <summary>
    /// Whether the named tool is safe for a read-only client. Unknown names are
    /// treated as <b>not</b> read-only (fail closed), so a read-only client never
    /// runs a tool it cannot prove is side-effect-free.
    /// </summary>
    public bool IsToolReadOnly(string toolName) => _readOnlyTools.Contains(toolName);

    /// <summary>
    /// Builds a tool host over the given Avalonia application + Core spine. Registers
    /// the same DI singletons <c>McpHost</c> does (Application, options, registry,
    /// serializer, optional binding-error sink, and the <see cref="IUiAdapter"/>),
    /// then materializes every <c>[McpServerTool]</c> in the Core tools assembly.
    /// </summary>
    public static ClientToolHost Build(
        Avalonia.Application app,
        AvaloniaMcp.Core.McpServerOptions options,
        params Assembly[] additionalToolAssemblies)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        var registry = new ControlRegistry();
        var serializer = new PropertyValueSerializer(options.MaxSerializationDepth);
        BindingErrorSink? sink = options.CaptureBindingErrors
            ? BindingErrorSink.Current ?? BindingErrorSink.Install(options.BindingErrorBufferSize)
            : null;

        var sc = new ServiceCollection();
        sc.AddSingleton(app);
        sc.AddSingleton(options);
        sc.AddSingleton(registry);
        sc.AddSingleton(serializer);
        if (sink is not null)
            sc.AddSingleton(sink);
        sc.AddSingleton<IUiAdapter>(new AvaloniaUiAdapter(serializer, sink, options.MaxScreenshotDimension));

        var provider = sc.BuildServiceProvider();

        var assemblies = new List<Assembly> { typeof(AvaloniaMcp.Core.Tools.InspectionTools).Assembly };
        assemblies.AddRange(additionalToolAssemblies);

        var tools = new Dictionary<string, McpServerTool>(StringComparer.Ordinal);
        var readOnly = new HashSet<string>(StringComparer.Ordinal);
        var createOptions = new McpServerToolCreateOptions { Services = provider };

        foreach (var asm in assemblies.Distinct())
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
                    continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                {
                    if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
                        continue;

                    // Static tool methods need no target instance; the DI provider in
                    // createOptions.Services supplies the spine parameters, and the
                    // remaining parameters are bound from the JSON arguments at invoke.
                    var tool = method.IsStatic
                        ? McpServerTool.Create(method, target: null!, createOptions)
                        : McpServerTool.Create(method, ActivatorUtilities.CreateInstance(provider, type), createOptions);

                    var name = tool.ProtocolTool.Name;
                    tools[name] = tool;
                    if (IsReadOnly(tool))
                        readOnly.Add(name);
                }
            }
        }

        return new ClientToolHost(provider, tools, readOnly);
    }

    // The well-known mutating Core tools: the three ActionTools that change UI state
    // (set_property / automation_action / set_focus) plus every synthetic-input tool
    // (the "input/*" family). Anything not in this set and not annotated mutating is
    // treated as read-only. Kept as the wire (protocol) names, not method names.
    private static readonly HashSet<string> KnownMutatingTools = new(StringComparer.Ordinal)
    {
        "set_property", "automation_action", "set_focus",
        "pointer", "click_at", "scroll_at", "type_text", "send_keys",
    };

    /// <summary>
    /// Classifies a built tool as read-only. The MCP <see cref="ToolAnnotations.ReadOnlyHint"/>
    /// wins when the author set it; otherwise the tool is read-only iff it is not one
    /// of the <see cref="KnownMutatingTools"/>. The Core tools carry no annotations
    /// today, so the explicit set is the operative rule — but a future annotated tool
    /// is honoured without touching this code.
    /// </summary>
    private static bool IsReadOnly(McpServerTool tool)
    {
        var hint = tool.ProtocolTool.Annotations?.ReadOnlyHint;
        if (hint is not null)
            return hint.Value;

        return !KnownMutatingTools.Contains(tool.ProtocolTool.Name);
    }

    /// <summary>
    /// Projects the built tools to the wire <see cref="ToolDescriptor"/> list the
    /// client sends in a <see cref="ToolListMessage"/>. The input schema is copied
    /// straight from <see cref="McpServerTool.ProtocolTool"/> (<see cref="Tool.InputSchema"/>).
    /// </summary>
    public IReadOnlyList<ToolDescriptor> Describe()
    {
        var list = new List<ToolDescriptor>(_tools.Count);
        foreach (var tool in _tools.Values)
        {
            var pt = tool.ProtocolTool;
            list.Add(new ToolDescriptor
            {
                Name = pt.Name,
                Description = pt.Description,
                // Tool.InputSchema is a JsonElement; clone so it survives the source's lifetime.
                InputSchema = pt.InputSchema.Clone(),
            });
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>
    /// Invokes the named tool with <paramref name="argumentsJson"/> (a JSON object,
    /// or null/undefined for no args). Returns the MCP <see cref="CallToolResult"/>;
    /// throws <see cref="KeyNotFoundException"/> if the tool is unknown.
    /// </summary>
    public async Task<CallToolResult> InvokeAsync(
        string toolName, JsonElement? argumentsJson, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
            throw new KeyNotFoundException($"Tool '{toolName}' is not exposed by this client.");

        var args = ToArgumentDictionary(argumentsJson);
        var requestParams = new CallToolRequestParams
        {
            Name = toolName,
            Arguments = args,
        };

        var request = ToolInvoker.CreateRequest(requestParams, _services);
        return await tool.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static IDictionary<string, JsonElement>? ToArgumentDictionary(JsonElement? argumentsJson)
    {
        if (argumentsJson is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }

    /// <inheritdoc/>
    public void Dispose() => (_services as IDisposable)?.Dispose();
}
