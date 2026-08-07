namespace Keincheck.Hub;

/// <summary>
/// Owns the hub's long-lived MCP server objects so <see cref="Program"/> and the
/// (future) tray UI can start/stop them independently of the Avalonia lifetime.
/// </summary>
public static class HubRuntime
{
    private static HubMcpServer? _mcp;
    private static HubPipeMcpListener? _pipe;
    private static IClientBroker? _broker;

    /// <summary>The active broker, once started.</summary>
    public static IClientBroker? Broker => _broker;

    /// <summary>The MCP server, once started.</summary>
    public static HubMcpServer? Mcp => _mcp;

    /// <summary>
    /// The remote-access facility, when the hub build has one. Null in tests and in any host
    /// that never constructed it — the <c>hub_remote_*</c> meta-tools then report remote as
    /// unavailable rather than failing.
    /// </summary>
    public static Remote.RemoteAccess? Remote { get; set; }

    /// <summary>Who is currently allowed to drive each app instance, once started.</summary>
    public static ClientClaimRegistry? Claims { get; private set; }

    /// <summary>Starts the MCP servers around <paramref name="broker"/>.</summary>
    public static void Start(IClientBroker broker, HubOptions options)
    {
        _broker = broker;

        // One registry, shared by the MCP server (which enforces claims), the broker (which
        // reads them to resolve "an instance nobody is driving") and the tray (which shows
        // them and can force-release). Constructing it here keeps that single instance
        // obvious rather than leaving each component to make its own.
        var claims = new ClientClaimRegistry(
            options.ClaimIdleTimeout, (broker as PipeClientBroker)?.Audit);
        Claims = claims;

        _mcp = HubMcpServer.Start(broker, options, claims);

        if (options.ServeMcpOverPipe)
        {
            _pipe = new HubPipeMcpListener(_mcp, options);
            _pipe.Start();
        }
    }

    /// <summary>Stops everything started by <see cref="Start"/>.</summary>
    public static async Task StopAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_mcp is not null) await _mcp.DisposeAsync().ConfigureAwait(false);
        _pipe = null;
        _mcp = null;
    }
}
