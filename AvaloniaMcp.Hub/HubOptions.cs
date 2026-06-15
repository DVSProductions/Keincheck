namespace AvaloniaMcp.Hub;

/// <summary>
/// Configuration for the hub's MCP server + transports.
/// </summary>
public sealed class HubOptions
{
    /// <summary>Loopback port for the HTTP MCP endpoint. Default 3100.</summary>
    public int HttpPort { get; set; } = 3100;

    /// <summary>
    /// When true, also run an MCP server over the control pipe so the stdio shim can
    /// be a pure byte-pump (the strongly-preferred Mechanic #3 path). Default true.
    /// </summary>
    public bool ServeMcpOverPipe { get; set; } = true;

    /// <summary>The named pipe to serve/connect on. Defaults to <c>PipeNames.ControlPipe</c>.</summary>
    public string? PipeName { get; set; }

    /// <summary>
    /// The MCP server's advertised name/version (shown to the AI client on initialize).
    /// </summary>
    public string ServerName { get; set; } = "AvaloniaMcp.Hub";

    /// <summary>The server version string surfaced on initialize.</summary>
    public string ServerVersion { get; set; } = "0.1.0";

    /// <summary>How long to wait for a client's tool result before failing the call. Default 60s.</summary>
    public TimeSpan InvokeTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// When true the hub prefixes proxied tool names with the active client id
    /// (e.g. <c>app1.get_logical_tree</c>) so multiple clients never collide. When
    /// false the active client's tool names are passed through verbatim. Default false
    /// (single active client at a time).
    /// </summary>
    public bool QualifyToolNames { get; set; }
}
