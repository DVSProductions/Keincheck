using AvaloniaMcp.Protocol;

namespace AvaloniaMcp.Hub;

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

    /// <summary>True if the hub or the client has marked this app read-only (mutating tools refused).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The client's last-reported tool catalog (what the hub advertises when this client is active).</summary>
    public IReadOnlyList<ToolDescriptor> Tools { get; init; } = Array.Empty<ToolDescriptor>();

    /// <summary>The executable path recorded for launch/restart, when known.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>UTC time of the last heartbeat or message from this client.</summary>
    public DateTimeOffset LastSeenUtc { get; init; }
}
