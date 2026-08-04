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
    /// The client whose tools the hub is currently advertising to the AI (the
    /// "active" client). Null when no client is selected/connected. Setting it makes
    /// the broker raise an active-changed signal so the MCP server re-lists tools.
    /// </summary>
    string? ActiveClientId { get; set; }

    /// <summary>
    /// Launches a known app by id (e.g. via its recorded executable path). Returns
    /// the launched process id, or throws if the app is unknown / cannot be started.
    /// The client connects back over the pipe on its own.
    /// </summary>
    Task<int> LaunchClientAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts a client: signals/terminates the running instance (if any) and
    /// launches it again. Returns the new process id.
    /// </summary>
    Task<int> RestartClientAsync(string clientId, CancellationToken cancellationToken = default);

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
    /// Forwards a tool call to the owning client over the pipe and awaits its
    /// <see cref="ToolResultMessage"/>. Throws if the client is not connected, the
    /// client is read-only and the tool mutates, or the call times out.
    /// </summary>
    Task<ToolResultMessage> InvokeOnClientAsync(
        string clientId, string toolName, System.Text.Json.JsonElement? argumentsJson,
        CancellationToken cancellationToken = default);

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

/// <summary>How a client is attached to the hub.</summary>
public enum ClientTransport
{
    /// <summary>The local control pipe — same machine, same user. The default and fast path.</summary>
    Pipe = 0,

    /// <summary>A mutually-authenticated TLS socket, possibly through an SSH tunnel.</summary>
    Tcp = 1,

    /// <summary>Reserved for a future dial-out rendezvous. Not implemented.</summary>
    Relay = 2,
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
}
