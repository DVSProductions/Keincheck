using System.Reflection;
using System.Text.Json;
using Keincheck.Core;
using Keincheck.Protocol;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Keincheck.Client;

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
    /// Builds a tool host over an injected, framework-specific <see cref="IUiAdapter"/>
    /// + <see cref="IUiDispatcher"/> and the Core spine. Registers the DI singletons the
    /// tools consume (options, registry, the adapter, the dispatcher), then materializes
    /// every <c>[McpServerTool]</c> in the Core tools assembly. No UI-toolkit type is
    /// referenced here — the caller (framework glue) supplies the adapter/dispatcher.
    /// </summary>
    public static ClientToolHost Build(
        IUiAdapter adapter,
        IUiDispatcher dispatcher,
        Keincheck.Core.McpServerOptions options,
        params Assembly[] additionalToolAssemblies)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);

        var registry = new ControlRegistry();

        var sc = new ServiceCollection();
        sc.AddSingleton(options);
        sc.AddSingleton(registry);
        sc.AddSingleton(adapter);
        sc.AddSingleton(dispatcher);

        var provider = sc.BuildServiceProvider();

        var assemblies = new List<Assembly> { typeof(Keincheck.Core.Tools.InspectionTools).Assembly };
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
    /// The Core tools that are known to be side-effect-free. Kept as an explicit allow-list
    /// alongside <see cref="KnownMutatingTools"/> so that a tool in <i>neither</i> list is
    /// treated as mutating rather than assumed safe.
    /// </summary>
    private static readonly HashSet<string> KnownReadOnlyTools = new(StringComparer.Ordinal)
    {
        "list_windows", "query_controls", "hit_test", "wait_for", "wait_for_idle",
        "describe_screen", "keincheck_guide",
    };

    /// <summary>
    /// Classifies a built tool as read-only. An explicit MCP
    /// <see cref="ToolAnnotations.ReadOnlyHint"/> always wins; otherwise the tool must be
    /// recognisably read-only (a <c>get_</c>/<c>screenshot_</c> reader, or one of
    /// <see cref="KnownReadOnlyTools"/>) to qualify.
    /// </summary>
    /// <remarks>
    /// <b>Fail closed.</b> This previously returned "read-only" for anything not in
    /// <see cref="KnownMutatingTools"/>, which was tolerable while the classification was only
    /// used for this client's own local gate over a fixed Core tool set. It stopped being
    /// tolerable once the result is <i>reported to the hub</i> (<see cref="Describe"/>) and the
    /// hub trusts it: <see cref="Build"/> accepts <c>additionalToolAssemblies</c>, so an app's
    /// own unannotated mutating tool would have been declared read-only and run against a
    /// read-only client — including a remote one, where read-only is the default rather than
    /// an unusual setting. An unrecognised name is now mutating, and an app that wants
    /// otherwise says so with a <c>ReadOnlyHint</c>.
    /// </remarks>
    private static bool IsReadOnly(McpServerTool tool)
    {
        var hint = tool.ProtocolTool.Annotations?.ReadOnlyHint;
        if (hint is not null)
            return hint.Value;

        var name = tool.ProtocolTool.Name;
        if (KnownMutatingTools.Contains(name))
            return false;

        return name.StartsWith("get_", StringComparison.Ordinal)
            || name.StartsWith("screenshot", StringComparison.OrdinalIgnoreCase)
            || KnownReadOnlyTools.Contains(name);
    }

    /// <summary>
    /// Projects the built tools to the wire <see cref="ToolDescriptor"/> list the
    /// client sends in a <see cref="ToolListMessage"/>. The input schema is copied
    /// straight from <see cref="McpServerTool.ProtocolTool"/> (<see cref="Tool.InputSchema"/>).
    /// </summary>
    /// <remarks>
    /// Each descriptor also carries <see cref="ToolDescriptor.ReadOnly"/>, the same
    /// classification this host uses for its own local gate. The client is the right
    /// authority: it owns the tool implementations, whereas the hub could previously only
    /// guess from names — and guessed wrong for <c>describe_screen</c>, <c>wait_for_idle</c>
    /// and others. That matters now that remote clients are read-only by default.
    /// </remarks>
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
                ReadOnly = _readOnlyTools.Contains(pt.Name),
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
