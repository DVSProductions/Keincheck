namespace Keincheck.Protocol;

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
    /// shim) connect to, e.g. <c>Keincheck.alice</c>. Pass this bare name to
    /// <see cref="System.IO.Pipes.NamedPipeServerStream"/> /
    /// <see cref="System.IO.Pipes.NamedPipeClientStream"/> (do <b>not</b> prefix
    /// <c>\\.\pipe\</c> — the BCL adds it).
    /// </summary>
    public static string ControlPipe { get; } = $"Keincheck.{UserScope}";

    /// <summary>
    /// A system-wide mutex name used by the hub for single-instance election and by
    /// the stdio shim to detect whether a hub is already running. Uses the
    /// <c>Global\</c> prefix so it spans terminal-server sessions for the same user.
    /// </summary>
    public static string SingleInstanceMutex { get; } = $"Global\\Keincheck.Hub.{UserScope}";

    /// <summary>
    /// Builds the name of a dedicated per-connection MCP pipe the hub can hand a
    /// freshly accepted MCP client (the stdio shim), keeping the control pipe free
    /// to accept the next connection. <paramref name="token"/> is an opaque,
    /// hub-chosen connection id.
    /// </summary>
    public static string McpSessionPipe(string token)
    {
        // The token is shortened to whatever the WHOLE name can still afford, not to a fixed
        // size. Capping each component independently is not enough: a 14-character login plus a
        // 20-character token already overflows a macOS socket path, and the failure is an
        // ArgumentOutOfRangeException from inside the socket layer that names only a path.
        var prefix = $"Keincheck.mcp.{UserScope}.";
        return prefix + Shorten(Sanitize(token), MaxNameLength - prefix.Length);
    }

    /// <summary>
    /// The environment variable the hub sets on a process it launches, and that the client
    /// echoes back in <see cref="RegisterMessage.LaunchToken"/>. It is how the hub knows
    /// which registration belongs to which launch when the started process is not the one
    /// that ends up registering (a launcher script, a <c>dotnet</c> host, a shim).
    /// </summary>
    public const string LaunchTokenEnvVar = "KEINCHECK_LAUNCH_TOKEN";

    /// <summary>
    /// The longest a sanitized component may be before it is shortened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Unix, .NET implements named pipes as domain sockets at
    /// <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c>, and a domain socket path may be at most <b>104</b>
    /// characters on macOS. macOS gives each user a <c>TMPDIR</c> like
    /// <c>/var/folders/xx/…/T/</c> — 49 characters — which leaves roughly 44 for the whole
    /// name. Exceed it and the connect throws <see cref="ArgumentOutOfRangeException"/> from
    /// deep inside the socket layer, naming a path and nothing else.
    /// </para>
    /// <para>
    /// 20 is not arbitrary: Windows itself caps a SAM account name at 20 characters, so no
    /// Windows user has ever had a <see cref="UserScope"/> longer than this and no existing
    /// Windows pipe name changes. It only ever engages for a long POSIX login.
    /// </para>
    /// </remarks>
    internal const int MaxComponentLength = 20;

    /// <summary>
    /// The longest a whole pipe name may be: 104 (the macOS domain socket limit) minus a
    /// 49-character per-user <c>TMPDIR</c> minus the BCL's <c>CoreFxPipe_</c> prefix.
    /// </summary>
    internal const int MaxNameLength = 44;

    /// <summary>
    /// Sanitizes to letters, digits, dash and underscore, and shortens anything overlong to a
    /// stable, collision-resistant form so the resulting path fits a macOS domain socket.
    /// </summary>
    /// <remarks>
    /// Shortening is deterministic because both ends compute the name independently from the
    /// same inputs on the same machine — a random or time-based suffix would have the hub and
    /// the client listening and dialling different sockets.
    /// </remarks>
    private static string Sanitize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "default";

        Span<char> buf = stackalloc char[raw.Length];
        var n = 0;
        foreach (var c in raw)
            buf[n++] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        var clean = new string(buf[..n]);

        return Shorten(clean, MaxComponentLength);
    }

    /// <summary>
    /// Caps <paramref name="value"/> at <paramref name="max"/>, keeping a readable prefix and
    /// appending a hash of the original so two long values sharing a prefix stay distinct.
    /// </summary>
    /// <remarks>
    /// Plain truncation would map every token with the same leading characters onto one socket
    /// and silently attach a client to the wrong session. The hash is deterministic because
    /// both ends compute the name independently from the same inputs on the same machine — a
    /// random or time-based suffix would leave them listening and dialling different sockets,
    /// which presents as a hang rather than an error.
    /// </remarks>
    private static string Shorten(string value, int max)
    {
        if (value.Length <= max)
            return value;

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        var suffix = Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant(); // 8 chars

        // Too little room even for the hash: the hash alone is still unique and still fits.
        if (max <= suffix.Length)
            return suffix[..Math.Max(1, max)];

        return string.Concat(value.AsSpan(0, max - suffix.Length - 1), "_", suffix);
    }
}
