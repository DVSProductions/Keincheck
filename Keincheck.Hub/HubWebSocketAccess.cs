using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Keincheck.Hub;

/// <summary>Why a WebSocket attach attempt was refused, or that it was allowed.</summary>
public enum WebSocketGateResult
{
    /// <summary>The request may be upgraded and served.</summary>
    Allowed,

    /// <summary>The endpoint is switched off. The hub answers 404, as if it did not exist.</summary>
    Disabled,

    /// <summary>The page's <c>Origin</c> is not on the allowlist.</summary>
    OriginNotAllowed,

    /// <summary>No token, or one that matches nothing the hub issued.</summary>
    TokenRejected,
}

/// <summary>
/// The gate in front of the hub's WebSocket endpoint, plus the tokens and origins it is
/// checking against. JSON-backed under <c>%APPDATA%/Keincheck/websocket-access.json</c>,
/// following the <see cref="HubSettings"/> conventions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a gate exists at all.</b> The local pipe needs no such thing: it is
/// <c>PipeOptions.CurrentUserOnly</c>, so the operating system decides who may connect, and
/// anyone who passes that test can already do worse things directly. A TCP port has no
/// equivalent. Two callers can reach a loopback port that the pipe would have excluded:
/// </para>
/// <list type="number">
///   <item><b>Any local process</b>, running as any user on the machine. The token is what
///   stops it: a process that never received one cannot attach.</item>
///   <item><b>Any web page the user visits.</b> A browser will happily open
///   <c>ws://127.0.0.1:3100</c> from <c>https://evil.example</c> — the same-origin policy does
///   not apply to WebSockets, which is the cross-site WebSocket hijacking problem. The browser
///   does send <c>Origin</c>, and it cannot be forged by page script, so the allowlist is the
///   defence there.</item>
/// </list>
/// <para>
/// Both checks are required, because each covers what the other misses: a local process can
/// send any <c>Origin</c> it likes, and a hostile page cannot read a token it was never given.
/// </para>
/// <para>
/// <b>Off by default.</b> Like remote access, constructing this opens nothing — with no origin
/// approved and no token issued, the endpoint answers 404 and there is nothing to attach to.
/// </para>
/// </remarks>
public sealed class HubWebSocketAccess
{
    private readonly object _gate = new();
    private readonly string _path;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private bool _enabled;
    private readonly List<string> _origins = [];
    private readonly List<TokenRecord> _tokens = [];

    private HubWebSocketAccess(string path) => _path = path;

    /// <summary>The on-disk path the access policy is read from / written to.</summary>
    public string FilePath => _path;

    /// <summary>Whether the endpoint is served at all.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    /// <summary>The origins a browser page may attach from. Never wildcarded.</summary>
    public IReadOnlyList<string> AllowedOrigins
    {
        get { lock (_gate) return _origins.ToArray(); }
    }

    /// <summary>The labels of currently valid tokens. Never the token values themselves.</summary>
    public IReadOnlyList<string> TokenLabels
    {
        get { lock (_gate) return _tokens.Select(t => t.Label).ToArray(); }
    }

    /// <summary>Opens (and eagerly loads) the policy, or a custom path for tests.</summary>
    public static HubWebSocketAccess Open(string? path = null)
    {
        var access = new HubWebSocketAccess(path ?? DefaultPath());
        access.Load();
        return access;
    }

    /// <summary>The default per-user location, beside the hub's other state.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Keincheck", "websocket-access.json");

    // ------------------------------------------------------------------ the gate

    /// <summary>
    /// Decides whether a handshake may proceed. <paramref name="origin"/> is the request's
    /// <c>Origin</c> header, null when absent (a non-browser client).
    /// </summary>
    /// <remarks>
    /// A missing <c>Origin</c> is <b>not</b> treated as trusted. It means "not a browser", and a
    /// non-browser caller is exactly the local-process case that the token exists to stop — so
    /// it skips only the origin check, never the token check. Browsers always send the header,
    /// so a hostile page cannot get here by omitting it.
    /// </remarks>
    public WebSocketGateResult Check(string? origin, string? token)
    {
        lock (_gate)
        {
            // Pick up a policy written by another process since the last check. The build-time
            // `--issue-websocket-token` command runs as its own process and writes this file, so
            // without this a freshly enrolled app could not connect until the hub was restarted
            // -- which would make the whole build-time flow useless.
            ReloadIfChanged();

            if (!_enabled)
                return WebSocketGateResult.Disabled;

            if (origin is not null && !_origins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)))
                return WebSocketGateResult.OriginNotAllowed;

            if (string.IsNullOrEmpty(token) || !MatchesAnyToken(token))
                return WebSocketGateResult.TokenRejected;

            return WebSocketGateResult.Allowed;
        }
    }

    /// <summary>
    /// Compares against every stored token in fixed time, and does not stop at the first match.
    /// </summary>
    /// <remarks>
    /// Short-circuiting on a hit would leak, through timing, how far down the list a token sits
    /// — and comparing with <c>==</c> would leak how many leading characters a guess got right.
    /// The endpoint is reachable by any local process, so it is worth not being sloppy about it.
    /// </remarks>
    private bool MatchesAnyToken(string candidate)
    {
        var probe = Encoding.UTF8.GetBytes(candidate);
        var hit = false;
        foreach (var record in _tokens)
        {
            var known = Encoding.UTF8.GetBytes(record.Token);
            hit |= CryptographicOperations.FixedTimeEquals(probe, known);
        }
        return hit;
    }

    // ------------------------------------------------------------------ mutations

    /// <summary>
    /// Turns the endpoint on or off and persists immediately (best-effort).
    /// </summary>
    /// <param name="reason">
    /// Who turned it on and why, recorded alongside the flag. A build can enable the endpoint
    /// for a loopback origin, so the operator has to be able to find out that something did —
    /// this hub surfaces what it did rather than doing it quietly.
    /// </param>
    public void SetEnabled(bool value, string? reason = null)
    {
        lock (_gate)
        {
            if (_enabled == value && reason is null)
                return;
            _enabled = value;
            EnabledBy = value ? reason : null;
            EnabledAt = value ? DateTimeOffset.UtcNow : null;
            Save();
        }
    }

    /// <summary>Why the endpoint was last enabled, when something other than a human did it.</summary>
    public string? EnabledBy { get; private set; }

    /// <summary>When the endpoint was last enabled.</summary>
    public DateTimeOffset? EnabledAt { get; private set; }

    /// <summary>
    /// Adds an origin to the allowlist. Stored as scheme://host[:port] exactly as a browser
    /// sends it; a value that is not a well-formed absolute origin is refused rather than
    /// stored as something that can never match.
    /// </summary>
    public bool AllowOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
            return false;

        var normalized = parsed.GetLeftPart(UriPartial.Authority);
        lock (_gate)
        {
            if (_origins.Any(o => string.Equals(o, normalized, StringComparison.OrdinalIgnoreCase)))
                return true;
            _origins.Add(normalized);
            Save();
            return true;
        }
    }

    /// <summary>Removes an origin from the allowlist.</summary>
    public bool RevokeOrigin(string origin)
    {
        lock (_gate)
        {
            var removed = _origins.RemoveAll(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    /// <summary>
    /// Mints a token under <paramref name="label"/> and returns it. This is the only time the
    /// value is handed out — it is stored, but the hub never displays it again, so the label is
    /// what the operator revokes by.
    /// </summary>
    public string IssueToken(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        // 256 bits, base64url so it survives a query string without escaping.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        lock (_gate)
        {
            _tokens.Add(new TokenRecord { Label = label, Token = token });
            Save();
        }
        return token;
    }

    /// <summary>Revokes every token issued under <paramref name="label"/>.</summary>
    public bool RevokeToken(string label)
    {
        lock (_gate)
        {
            var removed = _tokens.RemoveAll(t => string.Equals(t.Label, label, StringComparison.Ordinal)) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    // ------------------------------------------------------------------ persistence

    // The write time the in-memory policy was loaded from, so an external write can be noticed
    // without re-reading and re-parsing the file on every single handshake.
    private DateTime _loadedStamp;

    /// <summary>Re-reads the policy if the file changed under us. Caller holds the lock.</summary>
    private void ReloadIfChanged()
    {
        try
        {
            var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : default;
            if (stamp != _loadedStamp)
                Load();
        }
        catch
        {
            // An unreadable timestamp is not a reason to refuse a request the in-memory policy
            // already allows; the next change will be picked up.
        }
    }

    private void Load()
    {
        try
        {
            _loadedStamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : default;

            if (!File.Exists(_path))
            {
                _enabled = false;
                _origins.Clear();
                _tokens.Clear();
                return;
            }

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path));
            if (dto is null)
                return;

            _enabled = dto.Enabled;
            EnabledBy = dto.EnabledBy;
            EnabledAt = dto.EnabledAt;
            _origins.Clear();
            _origins.AddRange(dto.Origins ?? []);
            _tokens.Clear();
            _tokens.AddRange(dto.Tokens ?? []);
        }
        catch
        {
            // Corrupt or unreadable: fall back to the closed default rather than to whatever
            // half-parsed. A policy file that cannot be read must not fail open.
            _enabled = false;
            _origins.Clear();
            _tokens.Clear();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var dto = new Dto
            {
                Enabled = _enabled,
                EnabledBy = EnabledBy,
                EnabledAt = EnabledAt,
                Origins = _origins.ToList(),
                Tokens = _tokens.ToList(),
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, s_json));

            // Our own write must not read back as somebody else's, or the next Check would
            // reload the file we just wrote and pointlessly re-parse it.
            _loadedStamp = File.GetLastWriteTimeUtc(_path);
        }
        catch
        {
            // Best-effort: a failed save means the policy does not survive a restart.
        }
    }

    private sealed class TokenRecord
    {
        public string Label { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
    }

    private sealed class Dto
    {
        public bool Enabled { get; set; }
        public string? EnabledBy { get; set; }
        public DateTimeOffset? EnabledAt { get; set; }
        public List<string>? Origins { get; set; }
        public List<TokenRecord>? Tokens { get; set; }
    }
}
