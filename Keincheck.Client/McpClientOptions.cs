namespace Keincheck.Client;

/// <summary>
/// Configuration for the in-app broker client (<see cref="AppBuilderClientExtensions.UseMcpClient"/>).
/// </summary>
public sealed class McpClientOptions
{
    /// <summary>
    /// A stable identifier the app picks for itself. If left null/empty the client
    /// derives one from the entry assembly name. The hub may further qualify it to
    /// keep ids unique across concurrently-running instances of the same app.
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>Human-readable name shown in the hub UI. Defaults to <see cref="AppId"/> when not set.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// When true the client advertises itself as read-only and refuses mutating tool
    /// invocations locally even if the hub forwards one. The hub also owns a
    /// per-app read-only toggle; either being set makes the app read-only.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>The pipe to reach the hub on. Defaults to <c>PipeNames.ControlPipe</c>.</summary>
    /// <remarks>Ignored when <see cref="Connector"/> is set to a non-pipe transport.</remarks>
    public string? PipeName { get; set; }

    /// <summary>
    /// How the client reaches the hub. Null (the default) means the local named pipe.
    /// </summary>
    /// <remarks>
    /// Set this to attach to a hub on <i>another machine</i>. The implementation lives in the
    /// separate <c>Keincheck.Remote</c> package — install it and call its <c>UseRemote(...)</c>
    /// extension. Keeping it out of this assembly is the point: an app that never references
    /// that package links no socket or TLS code, so it simply cannot be reached remotely.
    /// </remarks>
    public Keincheck.Protocol.IChannelConnector? Connector { get; set; }

    /// <summary>
    /// Called with a human-readable line whenever a connection attempt fails or a session ends.
    /// </summary>
    /// <remarks>
    /// Without this the only report is <c>Debug.WriteLine</c>, which is compiled out of a
    /// Release build — so a shipped app that cannot attach (a revoked or expired credential,
    /// the wrong address) fails in complete silence and simply never appears in the hub. The
    /// hub goes to the trouble of naming every refusal on the wire; something has to be able
    /// to surface it.
    /// </remarks>
    public Action<string>? Log { get; set; }

    /// <summary>How long to keep retrying the initial hub connection before giving up. Default 30s.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Heartbeat interval sent to the hub. Default 5s.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When true (default) the client auto-reconnects with backoff if the hub drops
    /// or restarts. When false a single disconnect ends the client session.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// The Core <see cref="Keincheck.Core.McpServerOptions"/> used to build the
    /// UI adapter (screenshot caps, binding-error capture, serialization depth).
    /// Created with defaults if not supplied.
    /// </summary>
    public Keincheck.Core.McpServerOptions? CoreOptions { get; set; }
}
