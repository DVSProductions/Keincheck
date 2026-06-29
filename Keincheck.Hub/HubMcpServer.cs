using System.Text.Json;
using Keincheck.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Keincheck.Tests")]

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
    private readonly HubRecorder _recorder = new();
    private WebApplication? _web;

    // The live MCP servers we can notify of list changes / log messages. Each session
    // registers its stable server once at the transport boundary (see RegisterSession) and
    // is removed when the session ends, so this stays bounded to the live session count.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<McpServer, byte> _servers = new();

    // Number of live MCP sessions currently registered for notifications (test seam).
    internal int ConnectedSessionCount => _servers.Count;

    /// <summary>
    /// Registers a live session's <b>stable</b> server so it receives list-changed / log
    /// notifications; pair with <see cref="UnregisterSession"/> when the session ends. Call
    /// once per session at the transport boundary (HubPipeMcpListener / the HTTP
    /// RunSessionHandler), never per request: <c>request.Server</c> is a fresh per-request
    /// wrapper, so adding it on every <c>tools/list</c> grew this set without bound.
    /// </summary>
    internal void RegisterSession(McpServer server) => _servers.TryAdd(server, 0);

    internal void UnregisterSession(McpServer server) => _servers.TryRemove(server, out _);

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

        // MCPEXP002: RunSessionHandler is marked experimental by the SDK, but it is the only
        // per-session start/completion hook. We use it to register each HTTP session's stable
        // server for notifications and remove it when the session ends — the same lifecycle the
        // pipe listener uses. Without it, HTTP sessions were only ever registered by the
        // per-request tools/list add, which leaked unboundedly.
#pragma warning disable MCPEXP002
        ConfigureMcp(builder.Services.AddMcpServer(ConfigureServerOptions))
            .WithHttpTransport(o =>
            {
                o.RunSessionHandler = async (_, server, ct) =>
                {
                    var mcp = (McpServer)server;
                    RegisterSession(mcp);
                    try { await mcp.RunAsync(ct).ConfigureAwait(false); }
                    finally { UnregisterSession(mcp); }
                };
            });
#pragma warning restore MCPEXP002

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
        // Session registration happens once per session at the transport boundary
        // (HubPipeMcpListener / the HTTP RunSessionHandler), NOT here: request.Server is a
        // fresh per-request wrapper, so adding it on every tools/list leaked unboundedly.
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

        // 1) Record/replay/export meta-tools are routed HERE, before the pure dispatcher,
        //    because they need the recorder/broker state the server owns.
        if (HandleRecordTool(name, args, ct) is { } recordResult)
            return await recordResult.ConfigureAwait(false);

        // 2) The remaining meta-tools are pure and dispatched in-hub.
        if (HubMetaTools.IsMetaTool(name))
        {
            return await HubMetaTools
                .DispatchAsync(_broker, name, args, RaiseListChanged, ct)
                .ConfigureAwait(false);
        }

        // 3) Everything else is a proxied tool. Resolve the target client: an explicit
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

            var result = ToCallToolResult(clientResult);

            // Capture the proxied step if a recording is active. We record the forwarded
            // (post-resolve) args and the success flag, never meta/record tools (they are
            // intercepted above and never reach this proxy path).
            _recorder.Capture(new RecordedStep
            {
                ClientId = targetId,
                ToolName = toolName,
                ArgsJson = toolArgs,
                Ok = result.IsError != true,
            });

            return result;
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

    // ---- record / replay / export -----------------------------------------

    /// <summary>
    /// Routes the recorder-backed meta-tools (record start/stop/status, replay, export).
    /// Returns the pending result task when <paramref name="name"/> is one of them, or
    /// <c>null</c> so the caller falls through to the pure dispatcher / proxy path.
    /// These live here (not in <see cref="HubMetaTools"/>) because they need the server's
    /// <see cref="HubRecorder"/> and the broker's invoke path.
    /// </summary>
    private ValueTask<CallToolResult>? HandleRecordTool(string name, JsonElement? args, CancellationToken ct)
    {
        return name switch
        {
            HubMetaTools.RecordStart  => ValueTask.FromResult(RecordStart(args)),
            HubMetaTools.RecordStop   => ValueTask.FromResult(RecordStop()),
            HubMetaTools.RecordStatus => ValueTask.FromResult(RecordStatus()),
            HubMetaTools.Replay       => ReplayAsync(args, ct),
            HubMetaTools.ExportTest   => ValueTask.FromResult(ExportTest(args)),
            _ => null,
        };
    }

    private CallToolResult RecordStart(JsonElement? args)
    {
        var name = TryGetString(args, "name");
        _recorder.Start(name);
        return HubMetaTools.JsonResult(new { recording = true, name });
    }

    private CallToolResult RecordStop()
    {
        var steps = _recorder.Stop();
        return HubMetaTools.JsonResult(new { recording = false, steps });
    }

    private CallToolResult RecordStatus() =>
        HubMetaTools.JsonResult(new
        {
            recording = _recorder.IsRecording,
            steps = _recorder.Count,
            name = _recorder.Name,
        });

    /// <summary>
    /// Re-issues every buffered step to its original client, in order. Steps whose client
    /// is no longer connected are labelled skipped instead of failing the whole replay.
    /// </summary>
    private async ValueTask<CallToolResult> ReplayAsync(JsonElement? args, CancellationToken ct)
    {
        var stopOnError = TryGetBool(args, "stopOnError") ?? false;
        var delayMs = Math.Max(0, TryGetInt(args, "delayMs") ?? 0);

        var steps = _recorder.Snapshot();
        var outcomes = new List<object>(steps.Count);
        int ok = 0, failed = 0, skipped = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            // A client that has since dropped can't service the step — skip, don't fail.
            if (_broker.ClientStatus(step.ClientId) is not { IsConnected: true })
            {
                skipped++;
                outcomes.Add(new { i, tool = step.ToolName, client = step.ClientId, ok = false, skipped = true, error = $"client '{step.ClientId}' not connected" });
                continue;
            }

            if (delayMs > 0 && i > 0)
                await Task.Delay(delayMs, ct).ConfigureAwait(false);

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_options.InvokeTimeout);

                var res = await _broker
                    .InvokeOnClientAsync(step.ClientId, step.ToolName, step.ArgsJson, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (res.IsError)
                {
                    failed++;
                    outcomes.Add(new { i, tool = step.ToolName, client = step.ClientId, ok = false, error = res.Error ?? "client reported an error" });
                    if (stopOnError) break;
                }
                else
                {
                    ok++;
                    outcomes.Add(new { i, tool = step.ToolName, client = step.ClientId, ok = true });
                }
            }
            catch (Exception ex)
            {
                failed++;
                outcomes.Add(new { i, tool = step.ToolName, client = step.ClientId, ok = false, error = ex.Message });
                if (stopOnError) break;
            }
        }

        return HubMetaTools.JsonResult(new
        {
            replayed = steps.Count,
            ok,
            failed,
            skipped,
            stopOnError,
            steps = outcomes,
        });
    }

    /// <summary>
    /// Exports the current recording: <c>"json"</c> yields a replayable scenario document,
    /// <c>"csharp"</c> yields a best-effort xUnit <c>[Fact]</c> skeleton (a starting point,
    /// not guaranteed to compile against any particular harness).
    /// </summary>
    private CallToolResult ExportTest(JsonElement? args)
    {
        var format = (TryGetString(args, "format") ?? "json").Trim().ToLowerInvariant();
        var steps = _recorder.Snapshot();

        if (format == "csharp")
            return HubMetaTools.JsonResult(new { format = "csharp", code = BuildCSharpSkeleton(steps, _recorder.Name) });

        // Default: a replayable JSON scenario document.
        var scenario = new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = 1,
            ["name"] = _recorder.Name,
        };
        var stepArray = new System.Text.Json.Nodes.JsonArray();
        foreach (var s in steps)
        {
            stepArray.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["clientId"] = s.ClientId,
                ["tool"] = s.ToolName,
                ["args"] = s.ArgsJson is { } a ? System.Text.Json.Nodes.JsonNode.Parse(a.GetRawText()) : null,
            });
        }
        scenario["steps"] = stepArray;

        return HubMetaTools.JsonResult(new
        {
            format = "json",
            scenario = JsonSerializer.SerializeToElement(scenario, ProtocolJson.Options),
        });
    }

    /// <summary>
    /// Builds a commented xUnit <c>[Fact]</c> skeleton that lists the recorded steps as
    /// structured invoke calls. It is explicitly a starting point — the assertion/harness
    /// wiring is left to the caller, so it is not guaranteed to compile as-is.
    /// </summary>
    private static string BuildCSharpSkeleton(IReadOnlyList<RecordedStep> steps, string? name)
    {
        var method = SanitizeIdentifier(name) is { Length: > 0 } id ? id : "RecordedScenario";
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("// Auto-generated from a Keincheck hub recording. STARTING POINT ONLY —");
        sb.AppendLine("// wire it to your own test harness/broker; it is not guaranteed to compile.");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine();
        sb.AppendLine("public class KeincheckScenarioTests");
        sb.AppendLine("{");
        sb.AppendLine("    [Fact]");
        sb.AppendLine($"    public async Task {method}()");
        sb.AppendLine("    {");
        sb.AppendLine("        // var broker = /* your IClientBroker */;");

        if (steps.Count == 0)
        {
            sb.AppendLine("        // (no steps were recorded)");
        }
        else
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                var argsLiteral = s.ArgsJson is { } a
                    ? $"\"\"\"{a.GetRawText()}\"\"\""
                    : "null";
                sb.AppendLine($"        // step {i}: {s.ToolName} on {s.ClientId} (recorded ok={s.Ok.ToString().ToLowerInvariant()})");
                sb.AppendLine($"        var args{i} = {argsLiteral} is string j{i} ? JsonSerializer.Deserialize<JsonElement>(j{i}) : (JsonElement?)null;");
                sb.AppendLine($"        var result{i} = await broker.InvokeOnClientAsync(\"{Escape(s.ClientId)}\", \"{Escape(s.ToolName)}\", args{i});");
                sb.AppendLine($"        Assert.False(result{i}.IsError);");
                sb.AppendLine();
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Reduces a recording name to a safe C# method identifier, or empty.</summary>
    private static string SanitizeIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? "" : char.IsDigit(s[0]) ? "_" + s : s;
    }

    // ---- small typed arg readers (for the record/replay tools) ------------

    private static string? TryGetString(JsonElement? args, string prop) =>
        args is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? TryGetBool(JsonElement? args, string prop) =>
        args is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static int? TryGetInt(JsonElement? args, string prop) =>
        args is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

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
