using Keincheck.Protocol;

namespace Keincheck.Hub;

/// <summary>
/// The Hub-internal seam the two Hub agents share. The <b>broker</b> (pipe server +
/// client registry + launcher) implements this; the <see cref="HubMcpServer"/> (MCP
/// meta-tools + dynamic proxy) consumes it. Phase-B Hub agents code against this
/// interface, not each other's concrete types.
/// </summary>
/// <remarks>
/// Threading: implementations must be safe to call from the ASP.NET request thread
/// pool (MCP handlers) and from the pipe-accept loop concurrently. Events may be
/// raised on arbitrary threads; subscribers marshal to the UI thread themselves.
/// </remarks>
public interface IClientBroker
{
    /// <summary>Currently connected clients (live pipe sessions).</summary>
    IReadOnlyList<ClientInfo> ListClients();

    /// <summary>
    /// All clients the hub has ever seen this run (and any persisted-known apps),
    /// including ones that have since disconnected. Used for launch/restart of apps
    /// that are not currently up.
    /// </summary>
    IReadOnlyList<ClientInfo> ListKnownClients();

    /// <summary>Returns the status of a single client by id, or null if unknown.</summary>
    ClientInfo? ClientStatus(string clientId);

    /// <summary>
    /// The hub-wide <b>default</b> selection: what a freshly connected agent starts out
    /// driving, and what the tray shows when no agent is connected. Null when nothing has
    /// been selected. Setting it raises a changed signal so the tray and any seeded session
    /// re-list.
    /// </summary>
    /// <remarks>
    /// This is deliberately <i>not</i> where tool calls are routed. Each MCP session owns its
    /// own selection (<see cref="HubSession.ActiveClientId"/>), because several agents share
    /// one hub and a single hub-wide slot meant one agent's <c>hub_select_client</c> silently
    /// retargeted every other agent's next call. What survives here is the auto-activation
    /// state machine — first client of the run, same client reclaiming the slot it dropped
    /// from, never a remote client on its own — which still decides what a new session is
    /// handed when it arrives.
    /// </remarks>
    string? DefaultClientId { get; set; }

    /// <summary>
    /// Launches a known app by id (e.g. via its recorded executable path). Returns
    /// the launched process id and a launch id, or throws if the app is unknown / cannot be
    /// started. The client connects back over the pipe on its own.
    /// </summary>
    Task<LaunchResult> LaunchClientAsync(
        string clientId, LaunchOptions? launch = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts a client: signals/terminates the running instance (if any) and
    /// launches it again. Returns the new process id.
    /// </summary>
    Task<LaunchResult> RestartClientAsync(
        string clientId, LaunchOptions? launch = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when a client that the hub launched on an agent's behalf finishes registering,
    /// so that agent (and only that agent) can adopt the instance.
    /// </summary>
    event EventHandler<ClientInfo>? LaunchRegistered;

    /// <summary>
    /// Blocks until a connected client matching <paramref name="appIdOrClientId"/> is
    /// available, then returns its snapshot. A null/empty filter resolves on the next
    /// client to connect (or any already-connected one). Used by <c>hub_wait_for_client</c>
    /// so the AI can launch/rebuild an app and block until its tools are back instead of
    /// polling <see cref="ListClients"/>.
    /// </summary>
    /// <param name="appIdOrClientId">
    /// The hub id (<c>AppId#n</c>) or bare app id to wait for; null/empty matches any client.
    /// </param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <returns>
    /// The matching client's snapshot, or <c>null</c> if the timeout elapsed first. Resolves
    /// immediately when a matching client is already connected.
    /// </returns>
    Task<ClientInfo?> WaitForClientAsync(
        string? appIdOrClientId, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks until a connected client matching <paramref name="filter"/> is available. The
    /// precise form, for when "any instance of this app" is not good enough — which is the
    /// normal case once several agents each run their own build of it.
    /// </summary>
    Task<ClientInfo?> WaitForClientAsync(
        ClientWaitFilter filter, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forwards a tool call to the owning client over the pipe and awaits its
    /// <see cref="ToolResultMessage"/>. Throws if the client is not connected, the
    /// client is read-only and the tool mutates, or the call times out.
    /// </summary>
    /// <param name="agent">
    /// The label of the agent session making the call, recorded in the audit trail. Optional
    /// so existing callers keep compiling; without it the trail cannot answer "which agent
    /// did that", which is the question that matters once several share one hub.
    /// </param>
    Task<ToolResultMessage> InvokeOnClientAsync(
        string clientId, string toolName, System.Text.Json.JsonElement? argumentsJson,
        CancellationToken cancellationToken = default, string? agent = null);

    /// <summary>Raised when a client connects (first <see cref="RegisterMessage"/>).</summary>
    event EventHandler<ClientInfo>? ClientConnected;

    /// <summary>
    /// Raised when a client's metadata or tool catalog changes (e.g. a new
    /// <see cref="ToolListMessage"/>, or a read-only toggle). Drives a
    /// <c>tools/list_changed</c> when the updated client is active.
    /// </summary>
    event EventHandler<ClientInfo>? ClientUpdated;

    /// <summary>Raised when a client disconnects (graceful goodbye or transport drop).</summary>
    event EventHandler<ClientInfo>? ClientDown;
}

/// <summary>
/// Which client an agent is waiting for. Every member narrows the match.
/// </summary>
/// <remarks>
/// A bare app id used to be the only filter, and it resolves to whichever matching instance
/// the registry happens to enumerate first. That was fine when one app meant one instance; it
/// is a coin toss once several agents each run their own worktree build, and losing the toss
/// means driving somebody else's app while believing it is yours.
/// </remarks>
public sealed record ClientWaitFilter
{
    /// <summary>The hub id, bare app id, or <c>AppId@Host</c> to match. Null matches any.</summary>
    public string? IdOrAppId { get; init; }

    /// <summary>
    /// Match only the instance produced by this launch (from <see cref="LaunchResult.LaunchId"/>).
    /// The precise answer to "the app I just started", and it beats every other filter.
    /// </summary>
    public string? LaunchId { get; init; }

    /// <summary>Match only a client running as this OS process.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Match only instances no agent is currently driving.</summary>
    public bool UnclaimedOnly { get; init; }

    /// <summary>Match only instances this agent already holds or launched.</summary>
    public bool MineOnly { get; init; }

    /// <summary>
    /// The asking agent. Used to evaluate <see cref="MineOnly"/> and to prefer that agent's own
    /// instance when several would otherwise match.
    /// </summary>
    public Guid? SessionId { get; init; }
}

/// <summary>
/// What an agent wants launched, when the recorded profile is not the whole story.
/// </summary>
/// <remarks>
/// The overrides exist for the multi-worktree case: several agents each build their own copy
/// of the same app and each needs to drive <i>its</i> build, but the persisted launch profile
/// records only one path — whichever copy registered most recently.
/// </remarks>
public sealed record LaunchOptions
{
    /// <summary>The executable to start instead of the recorded one.</summary>
    public string? ExePath { get; init; }

    /// <summary>The working directory to start it in.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Command-line arguments to pass.</summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// Allows <see cref="ExePath"/> to have a different file name than the recorded profile's.
    /// Off by default: the worktree case is the same binary in a different directory, so a
    /// different name usually means the wrong app is about to be started.
    /// </summary>
    public bool AllowDifferentExecutable { get; init; }

    /// <summary>The agent session asking for the launch, which will be given the instance.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>That agent's display label, for messages and the audit trail.</summary>
    public string? SessionLabel { get; init; }

    /// <summary>
    /// Only the owner of a claimed instance may restart it; force overrides that.
    /// </summary>
    public bool Force { get; init; }
}

/// <summary>The outcome of a launch: the process that was started, and the handle to wait on.</summary>
/// <param name="ProcessId">The OS process id the hub started.</param>
/// <param name="LaunchId">
/// The token to pass to <c>hub_wait_for_client</c> to resolve exactly this instance rather
/// than whichever copy of the app answers first.
/// </param>
public sealed record LaunchResult(int ProcessId, string LaunchId);

/// <summary>Thrown when an operation would disturb an app another agent is driving.</summary>
public sealed class ClientClaimedException : InvalidOperationException
{
    public ClientClaimedException(string message, ClaimInfo owner) : base(message) => Owner = owner;

    /// <summary>The agent that holds the instance.</summary>
    public ClaimInfo Owner { get; }
}

/// <summary>How a client is attached to the hub.</summary>
public enum ClientTransport
{
    /// <summary>The local control pipe — same machine, same user. The default and fast path.</summary>
    Pipe = 0,

    /// <summary>A mutually-authenticated TLS socket, possibly through an SSH tunnel.</summary>
    Tcp = 1,

    /// <summary>Reserved for a future dial-out rendezvous. Not implemented.</summary>
    Relay = 2,

    /// <summary>
    /// A WebSocket on the hub's loopback HTTP endpoint — the transport for apps that have no
    /// named pipes, which in practice means a browser. Authenticated by a hub-issued token and
    /// an origin allowlist rather than by an OS ACL or a client certificate.
    /// </summary>
    WebSocket = 3,
}

/// <summary>
/// What the listener established about a session before the client said a word — the facts
/// the client is not permitted to assert about itself.
/// </summary>
/// <remarks>
/// Everything here comes from the connection, not from <see cref="RegisterMessage"/>. That
/// separation is the point: <see cref="Host"/> is the common name of a certificate the hub
/// validated, so a remote client cannot claim to be a different machine, and
/// <see cref="CanLaunch"/> is decided by the transport rather than inferred later from an id.
/// </remarks>
public sealed record ClientSessionContext
{
    /// <summary>The local-pipe context: same machine, launchable, not read-only by default.</summary>
    public static readonly ClientSessionContext LocalPipe = new() { Transport = ClientTransport.Pipe };

    /// <summary>
    /// A session accepted on the loopback WebSocket endpoint.
    /// </summary>
    /// <remarks>
    /// <see cref="CanLaunch"/> is false: the peer is a page in a browser the hub did not start
    /// and cannot restart. <see cref="Host"/> stays null — unlike a TLS session there is no
    /// validated certificate to read a machine identity off, and the token says only that the
    /// operator issued it, not who is holding it.
    /// </remarks>
    public static ClientSessionContext ForWebSocket(string? peerAddress, string? origin) => new()
    {
        Transport = ClientTransport.WebSocket,
        PeerAddress = peerAddress,
        MachineId = origin,
        CanLaunch = false,
    };

    /// <summary>How this client is attached.</summary>
    public required ClientTransport Transport { get; init; }

    /// <summary>
    /// The authoritative host label, from the validated client certificate; null for the
    /// local pipe. Remote clients are filed as <c>AppId@Host#n</c>.
    /// </summary>
    public string? Host { get; init; }

    /// <summary>The machine name the client reported. Informational only — self-reported, so not trusted.</summary>
    public string? MachineId { get; init; }

    /// <summary>The peer's network address, for the audit trail. Always loopback over a tunnel.</summary>
    public string? PeerAddress { get; init; }

    /// <summary>
    /// Whether this client starts read-only. True for remote: "look at the remote machine" is always
    /// safe, and "drive the remote machine" should be a deliberate act.
    /// </summary>
    public bool ReadOnlyDefault { get; init; }

    /// <summary>
    /// Whether the hub may start or restart this client's process. False for remote — the
    /// process is on another machine.
    /// </summary>
    public bool CanLaunch { get; init; } = true;

    /// <summary>True for anything that is not the local pipe.</summary>
    public bool IsRemote => Transport != ClientTransport.Pipe;
}

/// <summary>
/// A snapshot of a client's state in the hub registry. Immutable record passed to
/// the MCP server and surfaced in the tray UI.
/// </summary>
public sealed record ClientInfo
{
    /// <summary>The stable hub-assigned id for this client (may qualify the app's self-id to stay unique).</summary>
    public required string ClientId { get; init; }

    /// <summary>The app's self-reported id (from <see cref="RegisterMessage.ClientId"/>).</summary>
    public string? AppId { get; init; }

    /// <summary>Human-readable display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The OS process id, when known.</summary>
    public int ProcessId { get; init; }

    /// <summary>True while a live pipe session exists for this client.</summary>
    public bool IsConnected { get; init; }

    /// <summary>
    /// True if this client currently owns one or more top-level windows (it is the
    /// UI-owning process). Reported by the client on each tool-list and recomputed as
    /// windows open after startup, so the AI can pick the window-owner among several
    /// clients of the same app without probing each with <c>list_windows</c>.
    /// </summary>
    public bool OwnsWindows { get; init; }

    /// <summary>True if the hub or the client has marked this app read-only (mutating tools refused).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// The informational version of the Keincheck.Client assembly this app is built against
    /// (e.g. "0.5.0"), as self-reported on register. Null when the client did not report one
    /// (older clients) or could not determine it. Purely informational.
    /// </summary>
    public string? ClientVersion { get; init; }

    /// <summary>The client's last-reported tool catalog (what the hub advertises when this client is active).</summary>
    public IReadOnlyList<ToolDescriptor> Tools { get; init; } = Array.Empty<ToolDescriptor>();

    /// <summary>The executable path recorded for launch/restart, when known.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>UTC time of the last heartbeat or message from this client.</summary>
    public DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>How this client is attached. <see cref="ClientTransport.Pipe"/> for local apps.</summary>
    public ClientTransport Transport { get; init; } = ClientTransport.Pipe;

    /// <summary>
    /// The machine this client runs on, for remote clients; null for local ones.
    /// </summary>
    /// <remarks>
    /// Taken from the common name of the certificate the hub validated — never self-reported,
    /// and never derived from the peer address, which is always loopback when the session
    /// arrives through an SSH tunnel. It is what makes <c>myapp@MACHINENAME</c> mean
    /// something.
    /// </remarks>
    public string? Host { get; init; }

    /// <summary>The machine name the client reported about itself. Informational only.</summary>
    public string? MachineId { get; init; }

    /// <summary>
    /// Whether the hub can launch or restart this client's process.
    /// </summary>
    /// <remarks>
    /// False for remote clients: the process is on another machine. It is a stored fact rather
    /// than something re-derived from the id, because the failure it prevents — starting a
    /// <i>local</i> copy of an app while the operator believes they restarted the remote one —
    /// is both silent and the worst outcome in this design.
    /// </remarks>
    public bool CanLaunch { get; init; } = true;

    /// <summary>True when this client is attached over something other than the local pipe.</summary>
    public bool IsRemote => Transport != ClientTransport.Pipe;

    /// <summary>
    /// The launch this instance came from, when the hub started it on an agent's behalf.
    /// Returned by <c>hub_launch_client</c> so that agent can wait for exactly the instance
    /// it asked for rather than whichever copy of the app happens to answer first.
    /// </summary>
    public string? LaunchId { get; init; }

    /// <summary>
    /// The agent session that launched this instance, when the hub started it. Null for a
    /// client that connected on its own.
    /// </summary>
    /// <remarks>
    /// This is what makes the worktree case work: three agents each build and launch their
    /// own copy of the same app, all three self-report the same app id, and they land as
    /// <c>myapp#1</c>/<c>#2</c>/<c>#3</c>. Recording who asked for which one lets the hub
    /// hand each agent its own instance — auto-selected and auto-claimed for them alone —
    /// instead of letting whoever registers first be adopted by everybody.
    /// </remarks>
    public Guid? LaunchSessionId { get; init; }

    /// <summary>The display label of the agent that launched this instance, for messages and the tray.</summary>
    public string? LaunchSessionLabel { get; init; }
}
