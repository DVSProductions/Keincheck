using Keincheck.Protocol;

namespace Keincheck.Hub;

/// <summary>
/// Configuration for the hub's MCP server + transports.
/// </summary>
public sealed class HubOptions
{
    /// <summary>Loopback port for the HTTP MCP endpoint. Default 3100.</summary>
    public int HttpPort { get; set; } = 3100;

    /// <summary>
    /// Path on the loopback HTTP endpoint where apps with no named pipes attach over a
    /// WebSocket. Serving it is gated by <see cref="HubWebSocketAccess"/>, which is off until
    /// an operator turns it on — this only decides where it would answer.
    /// </summary>
    public string WebSocketPath { get; set; } = WebSocketEndpoint.DefaultPath;

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
    public string ServerName { get; set; } = "Keincheck.Hub";

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

    /// <summary>
    /// When true (default), the hub advertises the <b>active client's</b> tools directly
    /// and emits <c>notifications/tools/list_changed</c> whenever the catalog changes
    /// (selection, connect, drop). MCP clients that do not handle dynamic tool additions
    /// never see those tools, so when false the hub serves a <b>static catalog</b>
    /// instead: only the meta-tools (including <c>hub_call_tool</c>, the generic proxy)
    /// are listed, the <c>listChanged</c> capability is not advertised, and no
    /// list-changed notifications are emitted.
    /// </summary>
    public bool DynamicTooling { get; set; } = true;

    /// <summary>
    /// When true, an <i>installed</i> hub polls GitHub for a newer release and applies it
    /// automatically — but only while no client is connected, so an AI session is never
    /// interrupted. A no-op for dev / non-Velopack-installed runs. Default true.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>The GitHub repository the hub's Velopack releases are published to.</summary>
    public string UpdateRepoUrl { get; set; } = "https://github.com/DVSProductions/Keincheck";

    /// <summary>How often the auto-updater polls for a newer release. Default 1 hour.</summary>
    public TimeSpan UpdateCheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long an agent's write-claim on an app may sit untouched before another agent's
    /// mutating call may take it over. <see cref="Timeout.InfiniteTimeSpan"/> disables idle
    /// release. Default 15 minutes.
    /// </summary>
    /// <remarks>
    /// Long enough that an agent thinking, reading code, or waiting on a build does not lose
    /// the app it is driving; short enough that an agent which crashed without closing its
    /// transport does not wedge that app until the hub restarts. An agent that is genuinely
    /// finished should call <c>hub_release_client</c> rather than wait this out.
    /// </remarks>
    public TimeSpan ClaimIdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// When true (default), only one agent at a time may drive a given app instance. Turn it
    /// off to restore the previous free-for-all, in which concurrent agents can interleave
    /// pointer and keyboard input into one app and corrupt each other's capture and focus.
    /// </summary>
    public bool EnforceWriteClaims { get; set; } = true;
}
