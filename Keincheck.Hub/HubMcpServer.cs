using System.Text.Json;
using Keincheck.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Keincheck.Hub;

/// <summary>
/// Mechanic #2 — the hub's MCP server. It is a generic multiplexer: it advertises
/// the <i>active</i> client's tools (using the schemas the client reported in its
/// <see cref="ToolListMessage"/>) plus the hub's meta-tools, and forwards
/// <c>tools/call</c> to the owning client via <see cref="IClientBroker.InvokeOnClientAsync"/>.
/// Tool listing/dispatch is fully <b>dynamic</b> (handler hooks, not attribute
/// scanning), so the catalog changes as the active client changes; an active/catalog
/// change emits <c>notifications/tools/list_changed</c>. When a client drops, the hub
/// pushes an MCP logging notification naming <c>hub_restart_client</c>.
/// </summary>
/// <remarks>
/// The hub does NOT reference Core or Avalonia. It only knows the wire
/// <see cref="ToolDescriptor"/> schemas the clients report. Meta-tool names, schemas,
/// and dispatch live in <see cref="HubMetaTools"/>; this type owns the MCP transports,
/// session tracking, and the proxy of the active client's tools.
/// </remarks>
public sealed class HubMcpServer : IAsyncDisposable
{
    private readonly IClientBroker _broker;
    private readonly HubOptions _options;
    private WebApplication? _web;

    // The live MCP servers we can notify of list changes / log messages (each HTTP or
    // pipe session registers its server here on first tools/list).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<McpServer, byte> _servers = new();

    private HubMcpServer(IClientBroker broker, HubOptions options)
    {
        _broker = broker;
        _options = options;
        _broker.ClientUpdated += OnCatalogMayHaveChanged;
        _broker.ClientConnected += OnClientConnected;
        _broker.ClientDown += OnClientDown;
    }

    /// <summary>
    /// Starts the hub's loopback HTTP MCP server (and, by default, an MCP-over-pipe
    /// endpoint via the broker). Returns a handle; dispose to stop. The Phase-B Hub
    /// owns single-instance election and the tray UI around this.
    /// </summary>
    public static HubMcpServer Start(IClientBroker broker, HubOptions options)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(options);

        var hub = new HubMcpServer(broker, options);
        hub.StartHttp();
        return hub;
    }

    private void StartHttp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(System.Net.IPAddress.Loopback, _options.HttpPort));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        ConfigureMcp(builder.Services.AddMcpServer(ConfigureServerOptions))
            .WithHttpTransport();

        _web = builder.Build();
        _web.MapMcp();
        _web.Start();
    }

    private void ConfigureServerOptions(ModelContextProtocol.Server.McpServerOptions o)
    {
        o.ServerInfo = new Implementation { Name = _options.ServerName, Version = _options.ServerVersion };
        o.Capabilities ??= new ServerCapabilities();
        // Advertise that our tool list can change at runtime...
        o.Capabilities.Tools ??= new ToolsCapability();
        o.Capabilities.Tools.ListChanged = true;
        // ...and that we emit logging notifications (used to flag dropped clients).
        o.Capabilities.Logging ??= new LoggingCapability();
    }

    /// <summary>
    /// Wires the dynamic list/call handlers onto an MCP server builder. Exposed so a
    /// pipe/stream transport host can reuse the exact same handlers as the HTTP host.
    /// </summary>
    public IMcpServerBuilder ConfigureMcp(IMcpServerBuilder mcp)
    {
        return mcp
            .WithListToolsHandler(HandleListToolsAsync)
            .WithCallToolHandler(HandleCallToolAsync);
    }

    // ---- tools/list -------------------------------------------------------

    private ValueTask<ListToolsResult> HandleListToolsAsync(
        RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        // Track this session's server so we can notify it of list changes / logs.
        if (request.Server is { } srv)
            _servers.TryAdd(srv, 0);

        var result = new ListToolsResult { Tools = BuildToolList() };
        return ValueTask.FromResult(result);
    }

    private List<Tool> BuildToolList()
    {
        // Meta-tools first (always present), then the active client's tools verbatim.
        var tools = new List<Tool>(HubMetaTools.BuildCatalog());

        var activeId = _broker.ActiveClientId;
        if (activeId is not null && _broker.ClientStatus(activeId) is { IsConnected: true } active)
        {
            foreach (var d in active.Tools)
            {
                var name = _options.QualifyToolNames ? $"{active.ClientId}.{d.Name}" : d.Name;
                tools.Add(new Tool
                {
                    Name = name,
                    Description = d.Description,
                    InputSchema = d.InputSchema ?? HubMetaTools.EmptyObjectSchema(),
                });
            }
        }

        return tools;
    }

    // ---- tools/call -------------------------------------------------------

    private async ValueTask<CallToolResult> HandleCallToolAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var p = request.Params!;
        var name = p.Name;
        var args = ArgsToElement(p.Arguments);

        // 1) Meta-tools are dispatched in-hub.
        if (HubMetaTools.IsMetaTool(name))
        {
            return await HubMetaTools
                .DispatchAsync(_broker, name, args, RaiseListChanged, ct)
                .ConfigureAwait(false);
        }

        // 2) Everything else is a proxied tool. Resolve the target client: an explicit
        //    'client' argument overrides the active selection for this one call.
        var (targetId, toolArgs) = ResolveTarget(name, args);
        if (targetId is null)
        {
            return HubMetaTools.ErrorResult(
                $"No active client selected. Call {HubMetaTools.SelectClient} first, or pass a "
                + $"'{HubMetaTools.ClientOverrideArg}' argument naming the target client.");
        }

        // A call to a down/unknown client returns a structured error that names the
        // recovery tool instead of a raw transport failure.
        if (_broker.ClientStatus(targetId) is not { IsConnected: true })
            return HubMetaTools.DownClientError(targetId, "is not connected");

        var toolName = StripQualifier(name, targetId);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.InvokeTimeout);

            var clientResult = await _broker
                .InvokeOnClientAsync(targetId, toolName, toolArgs, timeoutCts.Token)
                .ConfigureAwait(false);

            return ToCallToolResult(clientResult);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return HubMetaTools.ErrorResult(
                $"Tool '{toolName}' on '{targetId}' timed out after {_options.InvokeTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            // Most likely the client dropped mid-call — point the AI at recovery.
            return _broker.ClientStatus(targetId) is { IsConnected: true }
                ? HubMetaTools.ErrorResult($"Invoke failed on '{targetId}': {ex.Message}")
                : HubMetaTools.DownClientError(targetId, $"dropped during the call ({ex.Message})");
        }
    }

    /// <summary>
    /// Picks the client a proxied call targets. A literal <c>client</c> property in the
    /// arguments wins (and is stripped from the args forwarded to the tool); otherwise
    /// the active client is used. When name-qualification is on, a qualified tool name
    /// (<c>app1.tool</c>) also names the client.
    /// </summary>
    private (string? targetId, JsonElement? args) ResolveTarget(string toolName, JsonElement? args)
    {
        // (a) qualified name carries the client id.
        if (_options.QualifyToolNames)
        {
            var dot = toolName.IndexOf('.');
            if (dot > 0)
                return (toolName[..dot], args);
        }

        // (b) explicit 'client' argument overrides the active selection.
        if (args is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty(HubMetaTools.ClientOverrideArg, out var c)
            && c.ValueKind == JsonValueKind.String
            && c.GetString() is { Length: > 0 } overrideId)
        {
            return (overrideId, RemoveProperty(obj, HubMetaTools.ClientOverrideArg));
        }

        // (c) fall back to the active client.
        return (_broker.ActiveClientId, args);
    }

    private string StripQualifier(string name, string clientId) =>
        _options.QualifyToolNames && name.StartsWith(clientId + ".", StringComparison.Ordinal)
            ? name[(clientId.Length + 1)..]
            : name;

    // ---- list_changed -----------------------------------------------------

    private void OnCatalogMayHaveChanged(object? sender, ClientInfo info)
    {
        // Only the active client's changes alter the advertised list.
        if (_broker.ActiveClientId is not null && info.ClientId != _broker.ActiveClientId)
            return;

        RaiseListChanged();
    }

    private void OnClientConnected(object? sender, ClientInfo info) => OnCatalogMayHaveChanged(sender, info);

    private void RaiseListChanged() => _ = NotifyToolListChangedAsync();

    /// <summary>
    /// Emits <c>notifications/tools/list_changed</c> to every connected MCP session.
    /// </summary>
    public async Task NotifyToolListChangedAsync()
    {
        foreach (var server in _servers.Keys)
        {
            try
            {
                await server.SendNotificationAsync(
                    NotificationMethods.ToolListChangedNotification, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                _servers.TryRemove(server, out _); // dead session
            }
        }
    }

    // ---- client down -> logging notification ------------------------------

    private void OnClientDown(object? sender, ClientInfo info)
    {
        // If the active client dropped, its tools just disappeared from the catalog.
        if (info.ClientId == _broker.ActiveClientId)
            RaiseListChanged();

        _ = NotifyClientDownAsync(info);
    }

    /// <summary>
    /// Pushes a <c>notifications/message</c> (logging) to every session announcing the
    /// dropped client and naming <c>hub_restart_client('id')</c> as the recovery path.
    /// </summary>
    private async Task NotifyClientDownAsync(ClientInfo info)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            @event = "client_down",
            clientId = info.ClientId,
            displayName = info.DisplayName,
            message =
                $"Client '{info.ClientId}' disconnected. Call {HubMetaTools.RestartClient} "
                + $"with {{ \"clientId\": \"{info.ClientId}\" }} to bring it back.",
            recovery = new
            {
                tool = HubMetaTools.RestartClient,
                arguments = new { clientId = info.ClientId },
            },
        }, ProtocolJson.Options);

        var notification = new LoggingMessageNotificationParams
        {
            Level = LoggingLevel.Warning,
            Logger = _options.ServerName,
            Data = data,
        };

        foreach (var server in _servers.Keys)
        {
            try
            {
                await server.SendNotificationAsync(
                    NotificationMethods.LoggingMessageNotification, notification,
                    ProtocolJson.Options, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                _servers.TryRemove(server, out _); // dead session
            }
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static CallToolResult ToCallToolResult(ToolResultMessage msg)
    {
        if (msg.IsError)
            return HubMetaTools.ErrorResult(msg.Error ?? "client reported an error");

        // The client serialized MCP content blocks into msg.Content. Deserialize back.
        if (msg.Content is { ValueKind: JsonValueKind.Array } arr)
        {
            var blocks = arr.Deserialize<List<ContentBlock>>(ProtocolJson.Options) ?? new();
            return new CallToolResult { Content = blocks };
        }

        return HubMetaTools.JsonResult(msg.Content);
    }

    private static JsonElement? ArgsToElement(IDictionary<string, JsonElement>? args)
    {
        if (args is null || args.Count == 0)
            return null;
        var obj = new System.Text.Json.Nodes.JsonObject();
        foreach (var kv in args)
            obj[kv.Key] = System.Text.Json.Nodes.JsonNode.Parse(kv.Value.GetRawText());
        return JsonSerializer.SerializeToElement(obj);
    }

    /// <summary>Returns <paramref name="obj"/> without <paramref name="name"/> (or null if it then has no members).</summary>
    private static JsonElement? RemoveProperty(JsonElement obj, string name)
    {
        var node = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.Ordinal))
                continue;
            node[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        }
        return node.Count == 0 ? null : JsonSerializer.SerializeToElement(node);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _broker.ClientUpdated -= OnCatalogMayHaveChanged;
        _broker.ClientConnected -= OnClientConnected;
        _broker.ClientDown -= OnClientDown;

        if (_web is not null)
        {
            try
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _web.StopAsync(stopCts.Token).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            await _web.DisposeAsync().ConfigureAwait(false);
        }
    }
}
