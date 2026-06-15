namespace AvaloniaMcp.Protocol;

/// <summary>
/// The well-known endpoint names the broker uses to find the hub. Everything is
/// scoped to the current OS user so two people sharing a machine get independent
/// hubs and cannot see each other's apps.
/// </summary>
public static class PipeNames
{
    /// <summary>
    /// The base token every per-user endpoint name is derived from. Includes the
    /// user name so the pipe / mutex are unique per interactive user.
    /// </summary>
    public static string UserScope { get; } = Sanitize(Environment.UserName);

    /// <summary>
    /// The well-known named-pipe the hub listens on and clients (and the stdio
    /// shim) connect to, e.g. <c>AvaloniaMcp.alice</c>. Pass this bare name to
    /// <see cref="System.IO.Pipes.NamedPipeServerStream"/> /
    /// <see cref="System.IO.Pipes.NamedPipeClientStream"/> (do <b>not</b> prefix
    /// <c>\\.\pipe\</c> — the BCL adds it).
    /// </summary>
    public static string ControlPipe { get; } = $"AvaloniaMcp.{UserScope}";

    /// <summary>
    /// A system-wide mutex name used by the hub for single-instance election and by
    /// the stdio shim to detect whether a hub is already running. Uses the
    /// <c>Global\</c> prefix so it spans terminal-server sessions for the same user.
    /// </summary>
    public static string SingleInstanceMutex { get; } = $"Global\\AvaloniaMcp.Hub.{UserScope}";

    /// <summary>
    /// Builds the name of a dedicated per-connection MCP pipe the hub can hand a
    /// freshly accepted MCP client (the stdio shim), keeping the control pipe free
    /// to accept the next connection. <paramref name="token"/> is an opaque,
    /// hub-chosen connection id.
    /// </summary>
    public static string McpSessionPipe(string token) => $"AvaloniaMcp.mcp.{UserScope}.{Sanitize(token)}";

    private static string Sanitize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "default";

        Span<char> buf = stackalloc char[raw.Length];
        var n = 0;
        foreach (var c in raw)
            buf[n++] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        return new string(buf[..n]);
    }
}
