using ModelContextProtocol.Server;

namespace Keincheck.Hub;

/// <summary>
/// One AI agent's MCP session with the hub, and everything that belongs to that agent
/// alone: which client it selected, what it is recording, and the label it is known by.
/// </summary>
/// <remarks>
/// <para>
/// The hub is a per-user singleton, so several agents — several Claude Code windows, an
/// editor, a CI job — share one hub process and one client registry. Multiple <i>apps</i>
/// were always first-class here; multiple <i>agents</i> were not, because selection, the
/// recorder and the advertised tool list were single hub-wide slots. One agent calling
/// <c>hub_select_client</c> silently retargeted every other agent's next tool call.
/// </para>
/// <para>
/// This type is where that state moved to. A session is created at the transport boundary
/// (see <see cref="HubPipeMcpListener"/> and the HTTP <c>RunSessionHandler</c>), lives for
/// exactly one MCP connection, and is discarded with it — so a recording dies with the
/// agent that started it.
/// </para>
/// <para><b>Threading.</b> Handlers run on the request thread pool while broker events
/// (client connected/down) arrive on arbitrary threads, so the mutable trio — selection,
/// reclaim memory, label — is guarded by one private lock. Every mutator is idempotent, so
/// a client-connected event racing session creation applies the same value twice and is
/// harmless.</para>
/// </remarks>
public sealed class HubSession
{
    private readonly object _gate = new();
    private string? _active;
    private string? _lastActive;
    private string _label = "";

    /// <summary>
    /// This session's stable identity. Used as the owner key for write-claims and to
    /// attribute audit entries, because it stays valid even before the session has been
    /// given a human-readable <see cref="Label"/>.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>When this agent connected.</summary>
    public DateTimeOffset ConnectedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// This agent's own recording buffer. Per-session so two agents recording at once
    /// produce two clean scenarios instead of one interleaved, unusable one.
    /// </summary>
    public HubRecorder Recorder { get; } = new();

    /// <summary>
    /// The session's <b>stable</b> MCP server — the one notifications are sent to. Null
    /// until the transport boundary attaches it, and never the per-request wrapper handlers
    /// receive.
    /// </summary>
    public McpServer? Server { get; private set; }

    internal void AttachServer(McpServer server) => Server = server;

    /// <summary>
    /// The human-readable name this agent is known by (e.g. <c>claude-code-2</c>), shown in
    /// contention errors, the audit trail and the tray. Empty until assigned: the MCP client
    /// only reports its name during <c>initialize</c>, which happens after the session exists.
    /// </summary>
    public string Label
    {
        get { lock (_gate) return _label; }
        internal set { lock (_gate) _label = value; }
    }

    /// <summary>
    /// The client this agent is driving. Every routing decision this session makes reads
    /// this and nothing global, which is what keeps two agents out of each other's way.
    /// </summary>
    public string? ActiveClientId
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>
    /// The client that was selected when it last dropped, so its reconnect can reclaim the
    /// selection automatically — this session's private copy of the auto-reselect memory the
    /// broker keeps for the hub as a whole.
    /// </summary>
    public string? LastActiveClientId
    {
        get { lock (_gate) return _lastActive; }
    }

    /// <summary>
    /// Selects a client for this session. A deliberate selection supersedes any pending
    /// auto-reselect target, so a previously-selected client reconnecting later will not
    /// steal this choice back.
    /// </summary>
    internal void SetActive(string? clientId)
    {
        lock (_gate)
        {
            _active = clientId;
            _lastActive = null;
        }
    }

    /// <summary>
    /// Applies the auto-activation rule to a newly connected client and reports whether this
    /// session's selection changed. A per-session mirror of the broker's rule: activate the
    /// first client of the run, or the same client reclaiming the slot it held when it
    /// dropped — never a different client stealing a deliberate selection, and never a remote
    /// client that was not already chosen.
    /// </summary>
    internal bool TryAutoSelect(ClientInfo info)
    {
        lock (_gate)
        {
            var mayAutoActivate = !info.IsRemote || info.ClientId == _lastActive;
            if (mayAutoActivate && _active is null && (_lastActive is null || info.ClientId == _lastActive))
            {
                _active = info.ClientId;
                _lastActive = null; // consumed — don't re-steal on a later reconnect
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Clears the selection if <paramref name="clientId"/> is the client this session was
    /// driving, remembering it so its reconnect reclaims the slot. Returns whether this
    /// session's advertised catalog changed.
    /// </summary>
    internal bool TryHandleClientDown(string clientId)
    {
        lock (_gate)
        {
            if (!string.Equals(_active, clientId, StringComparison.Ordinal))
                return false;
            _active = null;
            _lastActive = clientId;
            return true;
        }
    }

    /// <summary>A point-in-time projection for the tray and <c>hub_status</c>.</summary>
    public HubSessionInfo Snapshot()
    {
        lock (_gate)
        {
            return new HubSessionInfo(Id, _label, _active, Recorder.IsRecording, ConnectedUtc);
        }
    }
}

/// <summary>An immutable projection of one agent session, for display and status reporting.</summary>
/// <param name="Id">The session's stable identity (the write-claim owner key).</param>
/// <param name="Label">The human-readable agent name, or empty before <c>initialize</c> completed.</param>
/// <param name="ActiveClientId">The client this agent is driving, or null.</param>
/// <param name="IsRecording">Whether this agent has a recording in progress.</param>
/// <param name="ConnectedUtc">When the agent connected.</param>
public sealed record HubSessionInfo(
    Guid Id, string Label, string? ActiveClientId, bool IsRecording, DateTimeOffset ConnectedUtc);
