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

    /// <summary>Starts the MCP servers around <paramref name="broker"/>.</summary>
    public static void Start(IClientBroker broker, HubOptions options)
    {
        _broker = broker;
        _mcp = HubMcpServer.Start(broker, options);

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
