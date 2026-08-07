using System.Text.Json;
using Keincheck.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
/// change emits <c>notifications/tools/list_changed</c> — unless
/// <see cref="HubOptions.DynamicTooling"/> is off (static tooling mode), in which case
/// the catalog is fixed to the meta-tools and clients' tools stay reachable through the
/// <c>hub_call_tool</c> generic proxy. When a client drops, the hub
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
    private readonly ClientClaimRegistry _claims;
    private WebApplication? _web;

    /// <summary>Who is currently allowed to drive each app instance.</summary>
    public ClientClaimRegistry Claims => _claims;

    /// <summary>
    /// The gate in front of the client-attach WebSocket endpoint. Off until an operator enables
    /// it and approves an origin, so merely having this property changes nothing.
    /// </summary>
    public HubWebSocketAccess WebSocketAccess { get; } = HubWebSocketAccess.Open();

    // The live agent sessions, keyed by their STABLE server (the one notifications go to).
    // Each session is added once at the transport boundary (see RegisterSession) and removed
    // when it ends, so this stays bounded to the live session count.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<McpServer, HubSession> _sessions = new();

    // Fallback resolution channel. Both transports await RunAsync inside a method we own, so
    // setting this immediately before that await puts the session in the execution context
    // every handler invocation for the session inherits. Used only if the service-provider
    // and server-identity lookups both miss.
    private readonly AsyncLocal<HubSession?> _ambient = new();

    // Monotonic per-name counters behind agent labels ("claude-code-1", "claude-code-2").
    private readonly Dictionary<string, int> _labelCounters = new(StringComparer.OrdinalIgnoreCase);

    // Number of live MCP sessions currently registered for notifications (test seam).
    internal int ConnectedSessionCount => _sessions.Count;

    /// <summary>Raised when an agent connects, disconnects, or changes what it is driving.</summary>
    internal event Action? SessionsChanged;

    /// <summary>A point-in-time projection of every connected agent, for the tray and status.</summary>
    public IReadOnlyList<HubSessionInfo> SessionSnapshots() =>
        _sessions.Values.Select(s => s.Snapshot()).ToList();

    /// <summary>
    /// Creates the state object for one agent connection, seeded with the hub-wide default
    /// selection so a lone agent still finds a client already selected — the single-agent
    /// behaviour this whole mechanism has to leave untouched.
    /// </summary>
    internal HubSession CreateSession()
    {
        var session = new HubSession();
        session.SetActive(_broker.DefaultClientId);
        return session;
    }

    /// <summary>
    /// Binds a session to its <b>stable</b> server so it receives list-changed / log
    /// notifications; pair with <see cref="EndSession"/> when the session ends. Call once per
    /// session at the transport boundary (HubPipeMcpListener / the HTTP RunSessionHandler),
    /// never per request: <c>request.Server</c> is a fresh per-request wrapper, so adding it
    /// on every <c>tools/list</c> grew this collection without bound.
    /// </summary>
    internal void RegisterSession(McpServer server, HubSession session)
    {
        session.AttachServer(server);
        _sessions[server] = session;
        SessionsChanged?.Invoke();
    }

    /// <summary>Drops a finished session (and everything it owned: selection, recording, claims).</summary>
    internal void EndSession(HubSession session)
    {
        // Free whatever this agent was driving. Without this a crashed agent would hold its
        // app hostage until the idle timeout, and a tidy one would hold it forever.
        _claims.ReleaseSession(session.Id);

        if (session.Server is { } server)
            _sessions.TryRemove(server, out _);
        else
            foreach (var kv in _sessions.Where(kv => kv.Value == session).ToList())
                _sessions.TryRemove(kv.Key, out _);

        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Makes the session the ambient one for everything awaited inside
    /// <paramref name="body"/> — the last-resort resolution channel.
    /// </summary>
    internal async Task RunSessionAsync(HubSession session, Func<Task> body)
    {
        _ambient.Value = session;
        try { await body().ConfigureAwait(false); }
        finally { _ambient.Value = null; }
    }

    /// <summary>
    /// Finds the agent session a request belongs to.
    /// </summary>
    /// <remarks>
    /// Three channels, because <c>request.Server</c> is a per-request wrapper and is
    /// therefore never reference-equal to the stable server a session registered:
    /// <list type="number">
    ///   <item>the per-session service provider — the pipe listener builds one container per
    ///   session and registers the session in it, so this is exact and always hits there;</item>
    ///   <item>a direct hit on the stable server, for any transport that hands handlers the
    ///   real thing;</item>
    ///   <item>the transport's session id, which is what identifies a streamable-HTTP
    ///   session — that host shares one service container across sessions, and its handlers
    ///   run on ASP.NET request threads rather than inside the session's own
    ///   <c>RunAsync</c>, so neither of the first two channels reaches it;</item>
    ///   <item>the ambient execution-context value set around <c>RunAsync</c>, as a
    ///   last resort for any transport that dispatches from that loop.</item>
    /// </list>
    /// A null return means the request arrived on a session the hub is not tracking; callers
    /// degrade to the meta-tool-only catalog rather than guessing at someone else's selection.
    /// </remarks>
    private HubSession? ResolveSession(MessageContext ctx)
    {
        if (ctx.Services?.GetService(typeof(HubSession)) is HubSession fromContainer)
            return fromContainer;

        if (ctx.Server is { } server)
        {
            if (_sessions.TryGetValue(server, out var direct))
                return direct;

            if (server.SessionId is { Length: > 0 } sessionId)
            {
                foreach (var kv in _sessions)
                {
                    if (string.Equals(kv.Key.SessionId, sessionId, StringComparison.Ordinal))
                        return kv.Value;
                }
            }
        }

        return _ambient.Value;
    }

    /// <summary>
    /// Gives a session its display name on first use. Deferred rather than done at creation
    /// because the MCP client only reports who it is during <c>initialize</c>, which happens
    /// after the transport boundary has already built the session.
    /// </summary>
    private HubSession? EnsureLabel(HubSession? session)
    {
        if (session is null || session.Label.Length > 0)
            return session;

        var reported = session.Server?.ClientInfo?.Name;
        var name = SanitizeLabel(reported);
        lock (_labelCounters)
        {
            _labelCounters.TryGetValue(name, out var n);
            _labelCounters[name] = ++n;
            session.Label = $"{name}-{n}";
        }
        return session;
    }

    /// <summary>Reduces a client-reported name to a compact, log-safe label stem.</summary>
    private static string SanitizeLabel(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported))
            return "agent";

        var sb = new System.Text.StringBuilder(Math.Min(reported.Length, 32));
        foreach (var c in reported.Trim())
        {
            if (sb.Length >= 32)
                break;
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_';
            sb.Append(ok ? c : '-');
        }

        var result = sb.ToString().Trim('-', '.');
        return result.Length == 0 ? "agent" : result;
    }

    private HubMcpServer(IClientBroker broker, HubOptions options, ClientClaimRegistry? claims)
    {
        _broker = broker;
        _options = options;
        _claims = claims ?? new ClientClaimRegistry(
            options.ClaimIdleTimeout, (broker as PipeClientBroker)?.Audit);

        // The broker consults claims too, to resolve "an instance nobody is driving" for
        // wait filters. Point it at the same registry rather than leaving it to be wired up
        // separately, where forgetting would silently degrade those filters to "any instance".
        if (broker is PipeClientBroker pipeBroker)
            pipeBroker.Claims ??= _claims;
        _broker.ClientUpdated += OnCatalogMayHaveChanged;
        _broker.ClientConnected += OnClientConnected;
        _broker.ClientDown += OnClientDown;
        _broker.LaunchRegistered += OnLaunchRegistered;
    }

    /// <summary>
    /// An instance the hub started for one agent has registered. That agent gets it outright:
    /// selected and claimed, so it can drive its own build immediately without racing anyone.
    /// </summary>
    /// <remarks>
    /// This is what makes several agents on one codebase workable. Three agents each build
    /// their worktree and launch it; all three self-report the same app id and land as
    /// <c>myapp#1</c>/<c>#2</c>/<c>#3</c>. Without affinity each agent would have to guess
    /// which instance is its own — and the guess is not merely awkward, it is silently wrong,
    /// because driving a sibling's app looks like it worked.
    /// </remarks>
    private void OnLaunchRegistered(object? sender, ClientInfo info)
    {
        if (info.LaunchSessionId is not { } ownerId)
            return;

        var owner = _sessions.Values.FirstOrDefault(s => s.Id == ownerId);
        if (owner is null)
            return; // the agent gave up waiting and disconnected; leave the instance free

        owner.SetActive(info.ClientId);

        // force: the hub started this process a moment ago on this agent's behalf, so nobody
        // else can have a legitimate claim on it. A claim deliberately survives a client
        // disconnect (that is what lets a rebuild-relaunch loop keep the app you were
        // driving), which means a stale one from the previous incarnation is still sitting on
        // the hub id. Without force it would win, and a forced restart would kill another
        // agent's app and then refuse to let you drive the replacement.
        _claims.Acquire(info.ClientId, owner.Id, owner.Label, ClaimOrigin.Launch, force: true);

        SessionsChanged?.Invoke();
        RaiseListChanged(new[] { owner });
    }

    /// <summary>
    /// Starts the hub's loopback HTTP MCP server (and, by default, an MCP-over-pipe
    /// endpoint via the broker). Returns a handle; dispose to stop. The Phase-B Hub
    /// owns single-instance election and the tray UI around this.
    /// </summary>
    public static HubMcpServer Start(
        IClientBroker broker, HubOptions options, ClientClaimRegistry? claims = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(options);

        var hub = new HubMcpServer(broker, options, claims);
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
                    var session = CreateSession();
                    RegisterSession(mcp, session);
                    try
                    {
                        // The HTTP host shares one service container across sessions, so the
                        // ambient channel is what lets a handler tell these sessions apart.
                        await RunSessionAsync(session, () => mcp.RunAsync(ct)).ConfigureAwait(false);
                    }
                    finally { EndSession(session); }
                };
            });
#pragma warning restore MCPEXP002

        _web = builder.Build();
        _web.MapMcp();
        MapWebSocketAttach(_web);
        _web.Start();
    }

    /// <summary>
    /// The client-attach WebSocket endpoint, on the same loopback Kestrel as the MCP endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how an app with no named pipes reaches the hub — a browser-hosted Avalonia app,
    /// in practice. Above the socket nothing is new: the session becomes a
    /// <see cref="PipeChannel"/> over a <see cref="WebSocketStream"/> and goes into the same
    /// <c>AcceptChannel</c> the pipe listener and the TLS listener use.
    /// </para>
    /// <para>
    /// The route is always mapped; <see cref="HubWebSocketAccess"/> decides whether it answers.
    /// Mapping it conditionally would mean the hub had to restart to turn the feature on, and
    /// a disabled gate answers 404 anyway — indistinguishable from an unmapped route.
    /// </para>
    /// </remarks>
    private void MapWebSocketAttach(WebApplication app)
    {
        // Only the live broker can serve sessions; a stub broker (tests, design-time) has
        // nothing to attach to, so the route stays unmapped rather than accepting and hanging.
        if (_broker is not PipeClientBroker broker)
            return;

        app.UseWebSockets();

        app.Map(_options.WebSocketPath, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Absent Origin means "not a browser", which the gate treats as still needing a
            // token — it is not a way to skip the check.
            var origin = context.Request.Headers.Origin.ToString();
            origin = string.IsNullOrEmpty(origin) ? null : origin;
            var token = context.Request.Query[WebSocketEndpoint.TokenQueryParameter].ToString();

            var verdict = WebSocketAccess.Check(origin, token);
            if (verdict != WebSocketGateResult.Allowed)
            {
                // 404 for a disabled endpoint so a probe cannot tell the feature exists;
                // 403/401 once it is on, because by then the operator wants to see why their
                // own app was turned away.
                context.Response.StatusCode = verdict switch
                {
                    WebSocketGateResult.Disabled => StatusCodes.Status404NotFound,
                    WebSocketGateResult.OriginNotAllowed => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status401Unauthorized,
                };
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

            // ownsSocket: false — ASP.NET Core owns the socket for the request's lifetime, and
            // disposing it from under the framework closes the response mid-flight.
            var stream = new WebSocketStream(socket, ownsSocket: false);

            // Default limits, as the pipe uses. The TLS listener starts at ChannelLimits.Handshake
            // because it reads frames from a peer it has not authorized yet; here the token was
            // checked before the upgrade, so the first frame already comes from an allowed peer.
            using var channel = new PipeChannel(stream, ownsStream: true);

            await broker.AcceptChannel(
                channel,
                ClientSessionContext.ForWebSocket(
                    context.Connection.RemoteIpAddress?.ToString(), origin),
                context.RequestAborted).ConfigureAwait(false);
        });
    }

    private void ConfigureServerOptions(ModelContextProtocol.Server.McpServerOptions o)
    {
        o.ServerInfo = new Implementation { Name = _options.ServerName, Version = _options.ServerVersion };
        o.Capabilities ??= new ServerCapabilities();
        // Advertise that our tool list can change at runtime — but only in dynamic
        // tooling mode. In static mode the catalog is fixed, so we neither advertise
        // listChanged nor ever emit the notification (agents that cannot handle dynamic
        // tool additions keep working against the stable meta-tool catalog).
        o.Capabilities.Tools ??= new ToolsCapability();
        o.Capabilities.Tools.ListChanged = _options.DynamicTooling;
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
        var session = EnsureLabel(ResolveSession(request));
        var result = new ListToolsResult { Tools = BuildToolList(session) };
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// The catalog <paramref name="session"/> sees: the meta-tools, plus (in dynamic mode)
    /// the tools of the client <i>that agent</i> selected. Computed per session so one
    /// agent's selection cannot rewrite another's tool list mid-conversation.
    /// </summary>
    private List<Tool> BuildToolList(HubSession? session)
    {
        // Meta-tools first (always present). In dynamic tooling mode the active client's
        // tools are appended verbatim; in static mode the catalog stops here, so it is
        // identical for the whole session (client tools stay reachable via hub_call_tool).
        var tools = new List<Tool>(HubMetaTools.BuildCatalog());

        if (!_options.DynamicTooling)
            return tools;

        var activeId = session?.ActiveClientId;
        if (activeId is not null && _broker.ClientStatus(activeId) is { IsConnected: true } active)
        {
            foreach (var d in active.Tools)
            {
                // A client's tool catalog is entirely client-authored, so it can contain a
                // name that collides with one of the hub's own. Dispatch is already safe —
                // IsMetaTool is checked first, so the real meta-tool always runs — but
                // advertising the duplicate is both an MCP protocol violation and a way to put
                // an attacker-written description for, say, hub_remote_issue into the model's
                // context. Skip the shadow instead.
                if (!_options.QualifyToolNames && HubMetaTools.IsMetaTool(d.Name))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Hub] client '{active.ClientId}' advertises '{d.Name}', which collides " +
                        "with a hub meta-tool; not advertising it.");
                    continue;
                }

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
        var session = EnsureLabel(ResolveSession(request));

        if (session is null)
        {
            return HubMetaTools.ErrorResult(
                "This MCP session is not tracked by the hub, so it has no client selection of "
                + "its own. Reconnect and try again.");
        }

        // 1) Record/replay/export meta-tools are routed HERE, before the pure dispatcher,
        //    because they need the recorder/broker state the server owns.
        if (HandleRecordTool(session, name, args, ct) is { } recordResult)
            return await recordResult.ConfigureAwait(false);

        // 1b) hub_call_tool — the static-mode generic proxy — unwraps { tool, args, client }
        //     and joins the normal proxy path below.
        if (name == HubMetaTools.CallTool)
            return await HandleCallToolDispatchAsync(session, args, ct).ConfigureAwait(false);

        // 2) The remaining meta-tools are pure and dispatched in-hub.
        if (HubMetaTools.IsMetaTool(name))
        {
            var context = new HubToolContext(
                _broker, _claims, session, ConnectedSessionCount, _options,
                () => RaiseListChanged(new[] { session }));
            return await HubMetaTools.DispatchAsync(context, name, args, ct).ConfigureAwait(false);
        }

        // 3) Everything else is a proxied tool.
        return await InvokeProxiedToolAsync(session, name, args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unwraps a <c>hub_call_tool</c> call (<c>{ "tool": name, "args"?: {...},
    /// "client"?: id }</c>) and forwards it through the standard proxy path. The
    /// <c>client</c> member is re-attached to the forwarded arguments so the existing
    /// per-call override (<see cref="ResolveTarget"/>) applies unchanged.
    /// </summary>
    private ValueTask<CallToolResult> HandleCallToolDispatchAsync(
        HubSession session, JsonElement? args, CancellationToken ct)
    {
        var tool = TryGetString(args, "tool");
        if (string.IsNullOrEmpty(tool))
        {
            return ValueTask.FromResult(HubMetaTools.ErrorResult(
                $"{HubMetaTools.CallTool} requires a 'tool' argument naming the client tool "
                + $"to call (see {HubMetaTools.ListClientTools} for what a client offers)."));
        }

        JsonElement? forwarded = TryGetObject(args, "args");
        if (args is { ValueKind: JsonValueKind.Object } o
            && o.TryGetProperty(HubMetaTools.ClientOverrideArg, out var c)
            && c.ValueKind == JsonValueKind.String)
        {
            forwarded = WithProperty(forwarded, HubMetaTools.ClientOverrideArg, c.GetString()!);
        }

        return InvokeProxiedToolAsync(session, tool!, forwarded, ct);
    }

    /// <summary>
    /// The proxy path shared by directly-advertised client tools (dynamic mode) and
    /// <c>hub_call_tool</c> (any mode): resolve the target client, forward the call with
    /// the invoke timeout, capture the step when recording, and shape failures.
    /// </summary>
    private async ValueTask<CallToolResult> InvokeProxiedToolAsync(
        HubSession session, string name, JsonElement? args, CancellationToken ct)
    {
        // Resolve the target client: an explicit 'client' argument overrides this session's
        // selection for this one call.
        var (targetId, toolArgs) = ResolveTarget(session, name, args);
        if (targetId is null)
        {
            return HubMetaTools.ErrorResult(
                $"No active client selected. Call {HubMetaTools.SelectClient} first, or pass a "
                + $"'{HubMetaTools.ClientOverrideArg}' argument naming the target client.");
        }

        // A call to a down/unknown client returns a structured error that names the
        // recovery tool instead of a raw transport failure.
        // Pass the snapshot so the recovery hint fits the client: a remote one cannot be
        // restarted by the hub, so telling the model to try would be actively misleading.
        var known = _broker.ClientStatus(targetId);
        if (known is not { IsConnected: true })
            return HubMetaTools.DownClientError(targetId, "is not connected", known);

        var toolName = StripQualifier(name, targetId);

        // One driver per instance. Reads never contend, so they only keep the owner's claim
        // warm; a write takes the claim if the instance is free and is refused (with a way
        // out) if another agent is driving it.
        if (CheckClaim(session, known, toolName) is { } conflict)
            return conflict;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.InvokeTimeout);

            var clientResult = await _broker
                .InvokeOnClientAsync(targetId, toolName, toolArgs, timeoutCts.Token, session.Label)
                .ConfigureAwait(false);

            var result = ToCallToolResult(clientResult);

            // Capture the proxied step if THIS agent is recording. Per-session, so two agents
            // recording at once produce two clean scenarios rather than one interleaved mess.
            // We record the forwarded (post-resolve) args and the success flag, never
            // meta/record tools (they are intercepted above and never reach this proxy path).
            session.Recorder.Capture(new RecordedStep
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
            var status = _broker.ClientStatus(targetId);
            return status is { IsConnected: true }
                ? HubMetaTools.ErrorResult($"Invoke failed on '{targetId}': {ex.Message}")
                : HubMetaTools.DownClientError(targetId, $"dropped during the call ({ex.Message})", status);
        }
    }

    /// <summary>
    /// Applies the one-driver-per-instance rule to a call about to be forwarded. Returns the
    /// structured refusal when another agent owns the instance, or null to proceed.
    /// </summary>
    /// <remarks>
    /// A read-only client is skipped entirely: the broker refuses its mutating calls anyway,
    /// and taking a claim for a call that is about to be rejected would let an agent lock an
    /// app it is not even allowed to drive.
    /// </remarks>
    private CallToolResult? CheckClaim(HubSession session, ClientInfo target, string toolName)
    {
        var mutating = !ToolClassification.IsReadOnly(target.Tools, toolName);

        if (!mutating || !_options.EnforceWriteClaims || target.ReadOnly)
        {
            _claims.TouchIfOwner(target.ClientId, session.Id);
            return null;
        }

        if (_claims.CheckWrite(target.ClientId, session.Id, session.Label) is not ClaimDenied denied)
            return null;

        _claims.RecordDenied(target.ClientId, toolName, session.Label, denied.Owner);
        return HubMetaTools.ClaimConflictError(
            target.ClientId, toolName, denied,
            HubMetaTools.UnclaimedSiblings(_broker, _claims, target));
    }

    // ---- record / replay / export -----------------------------------------

    /// <summary>
    /// Routes the recorder-backed meta-tools (record start/stop/status, replay, export).
    /// Returns the pending result task when <paramref name="name"/> is one of them, or
    /// <c>null</c> so the caller falls through to the pure dispatcher / proxy path.
    /// These live here (not in <see cref="HubMetaTools"/>) because they need the server's
    /// <see cref="HubRecorder"/> and the broker's invoke path.
    /// </summary>
    private ValueTask<CallToolResult>? HandleRecordTool(
        HubSession session, string name, JsonElement? args, CancellationToken ct)
    {
        return name switch
        {
            HubMetaTools.RecordStart  => ValueTask.FromResult(RecordStart(session, args)),
            HubMetaTools.RecordStop   => ValueTask.FromResult(RecordStop(session)),
            HubMetaTools.RecordStatus => ValueTask.FromResult(RecordStatus(session)),
            HubMetaTools.Replay       => ReplayAsync(session, args, ct),
            HubMetaTools.ExportTest   => ValueTask.FromResult(ExportTest(session, args)),
            _ => null,
        };
    }

    private static CallToolResult RecordStart(HubSession session, JsonElement? args)
    {
        var name = TryGetString(args, "name");
        session.Recorder.Start(name);
        return HubMetaTools.JsonResult(new { recording = true, name });
    }

    private static CallToolResult RecordStop(HubSession session)
    {
        var steps = session.Recorder.Stop();
        return HubMetaTools.JsonResult(new { recording = false, steps });
    }

    private static CallToolResult RecordStatus(HubSession session) =>
        HubMetaTools.JsonResult(new
        {
            recording = session.Recorder.IsRecording,
            steps = session.Recorder.Count,
            name = session.Recorder.Name,
        });

    /// <summary>
    /// Re-issues every buffered step to its original client, in order. Steps whose client
    /// is no longer connected are labelled skipped instead of failing the whole replay.
    /// </summary>
    private async ValueTask<CallToolResult> ReplayAsync(
        HubSession session, JsonElement? args, CancellationToken ct)
    {
        var stopOnError = TryGetBool(args, "stopOnError") ?? false;
        var delayMs = Math.Max(0, TryGetInt(args, "delayMs") ?? 0);

        var steps = session.Recorder.Snapshot();
        var outcomes = new List<object>(steps.Count);
        int ok = 0, failed = 0, skipped = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            // A client that has since dropped can't service the step — skip, don't fail.
            if (_broker.ClientStatus(step.ClientId) is not { IsConnected: true } stepTarget)
            {
                skipped++;
                outcomes.Add(new { i, tool = step.ToolName, client = step.ClientId, ok = false, skipped = true, error = $"client '{step.ClientId}' not connected" });
                continue;
            }

            // Replay drives the app for real, so it obeys the same one-driver rule as a live
            // call — otherwise it would be the obvious way around the claim.
            if (CheckClaim(session, stepTarget, step.ToolName) is not null)
            {
                failed++;
                outcomes.Add(new { i, tool = step.ToolName, client = step.ClientId, ok = false, error = $"another agent is driving '{step.ClientId}'" });
                if (stopOnError) break;
                continue;
            }

            if (delayMs > 0 && i > 0)
                await Task.Delay(delayMs, ct).ConfigureAwait(false);

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_options.InvokeTimeout);

                var res = await _broker
                    .InvokeOnClientAsync(
                        step.ClientId, step.ToolName, step.ArgsJson, timeoutCts.Token, session.Label)
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
    private static CallToolResult ExportTest(HubSession session, JsonElement? args)
    {
        var format = (TryGetString(args, "format") ?? "json").Trim().ToLowerInvariant();
        var recorder = session.Recorder;
        var steps = recorder.Snapshot();

        if (format == "csharp")
            return HubMetaTools.JsonResult(new { format = "csharp", code = BuildCSharpSkeleton(steps, recorder.Name) });

        // Default: a replayable JSON scenario document.
        var scenario = new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = 1,
            ["name"] = recorder.Name,
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

    /// <summary>Reads an object property from a JSON object arg, or null.</summary>
    private static JsonElement? TryGetObject(JsonElement? args, string prop) =>
        args is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object
            ? v.Clone()
            : null;

    /// <summary>Returns <paramref name="obj"/> (or an empty object) with one string property added/replaced.</summary>
    private static JsonElement WithProperty(JsonElement? obj, string name, string value)
    {
        var node = new System.Text.Json.Nodes.JsonObject();
        if (obj is { ValueKind: JsonValueKind.Object } o)
        {
            foreach (var prop in o.EnumerateObject())
                node[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        }
        node[name] = value;
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// Picks the client a proxied call targets. A literal <c>client</c> property in the
    /// arguments wins (and is stripped from the args forwarded to the tool); otherwise
    /// <i>the calling agent's own</i> selection is used. When name-qualification is on, a
    /// qualified tool name (<c>app1.tool</c>) also names the client.
    /// </summary>
    private (string? targetId, JsonElement? args) ResolveTarget(
        HubSession session, string toolName, JsonElement? args)
    {
        // (a) qualified name carries the client id.
        if (_options.QualifyToolNames)
        {
            // The LAST dot, not the first: a remote client id embeds a host name, which may
            // itself contain dots (myapp@build.ci#1.get_logical_tree). Tool names never
            // contain a dot, so splitting from the right is unambiguous where splitting from
            // the left would truncate the client id to 'myapp@build'.
            var dot = toolName.LastIndexOf('.');
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

        // (c) fall back to the client THIS agent selected. Never the hub-wide default: that
        // is only a seed for new sessions, and routing on it is exactly how one agent's
        // hub_select_client used to retarget every other agent's next call.
        return (session.ActiveClientId, args);
    }

    private string StripQualifier(string name, string clientId) =>
        _options.QualifyToolNames && name.StartsWith(clientId + ".", StringComparison.Ordinal)
            ? name[(clientId.Length + 1)..]
            : name;
    // Note: this one is already correct for dotted client ids, because it matches the full
    // client id as a prefix rather than searching for a separator.

    // ---- list_changed -----------------------------------------------------

    /// <summary>
    /// A client's catalog or metadata changed: only the agents actually driving that client
    /// see a different tool list, so only they are notified.
    /// </summary>
    private void OnCatalogMayHaveChanged(object? sender, ClientInfo info)
    {
        RaiseListChanged(SessionsDriving(info.ClientId));
    }

    /// <summary>
    /// A client connected. Each agent independently decides whether to adopt it, following
    /// the same auto-activation rule the hub-wide default uses — except that an instance
    /// launched on behalf of one specific agent belongs to that agent alone, so nobody else
    /// auto-selects it out from under them.
    /// </summary>
    private void OnClientConnected(object? sender, ClientInfo info)
    {
        var changed = new List<HubSession>();
        foreach (var session in _sessions.Values)
        {
            if (info.LaunchSessionId is { } owner && owner != session.Id)
                continue; // somebody else asked for this instance

            if (session.TryAutoSelect(info))
                changed.Add(session);
        }

        if (changed.Count > 0)
            SessionsChanged?.Invoke();
        RaiseListChanged(changed);
    }

    /// <summary>The agents currently driving <paramref name="clientId"/>.</summary>
    private List<HubSession> SessionsDriving(string clientId) =>
        _sessions.Values
            .Where(s => string.Equals(s.ActiveClientId, clientId, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Points every agent at <paramref name="clientId"/>. The tray's "make active" affordance:
    /// a human sitting at the machine overrides what the agents chose, deliberately.
    /// </summary>
    public void SelectForAllSessions(string clientId)
    {
        var all = _sessions.Values.ToList();
        foreach (var session in all)
            session.SetActive(clientId);

        SessionsChanged?.Invoke();
        RaiseListChanged(all);
    }

    private void RaiseListChanged(IReadOnlyCollection<HubSession> targets)
    {
        // Static tooling mode: the catalog never changes, so never emit list_changed.
        if (!_options.DynamicTooling || targets.Count == 0)
            return;
        _ = NotifyToolListChangedAsync(targets);
    }

    /// <summary>
    /// Emits <c>notifications/tools/list_changed</c> to every connected MCP session. Kept as
    /// the broadcast form for callers that genuinely mean everyone; internal paths target
    /// only the sessions whose catalog actually changed.
    /// </summary>
    public Task NotifyToolListChangedAsync() => NotifyToolListChangedAsync(_sessions.Values.ToList());

    private async Task NotifyToolListChangedAsync(IReadOnlyCollection<HubSession> targets)
    {
        foreach (var session in targets)
        {
            if (session.Server is not { } server)
                continue;
            try
            {
                await server.SendNotificationAsync(
                    NotificationMethods.ToolListChangedNotification, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                _sessions.TryRemove(server, out _); // dead session
            }
        }
    }

    // ---- client down -> logging notification ------------------------------

    private void OnClientDown(object? sender, ClientInfo info)
    {
        // Every agent that was driving it just lost its tools; each remembers the id so the
        // client's reconnect reclaims that agent's selection.
        var affected = new List<HubSession>();
        foreach (var session in _sessions.Values)
        {
            if (session.TryHandleClientDown(info.ClientId))
                affected.Add(session);
        }

        if (affected.Count > 0)
            SessionsChanged?.Invoke();
        RaiseListChanged(affected);

        // The logging notification stays a broadcast: any agent may have been targeting this
        // client per-call with a "client" argument, without ever selecting it.
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

        foreach (var server in _sessions.Keys)
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
                _sessions.TryRemove(server, out _); // dead session
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
        _broker.LaunchRegistered -= OnLaunchRegistered;

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
