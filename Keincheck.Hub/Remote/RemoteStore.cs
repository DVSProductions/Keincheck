using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keincheck.Remote;

namespace Keincheck.Hub.Remote;

/// <summary>One credential the hub has issued, and whether it is still honoured.</summary>
public sealed record IssuedCredential
{
    /// <summary>The certificate serial (uppercase hex) — the stable revocation key.</summary>
    public required string Serial { get; init; }

    /// <summary>The host label the credential authenticates as.</summary>
    public required string Host { get; init; }

    /// <summary>When it was issued.</summary>
    public DateTimeOffset IssuedUtc { get; init; }

    /// <summary>When it stops being accepted on its own.</summary>
    public DateTimeOffset NotAfter { get; init; }

    /// <summary>How it was requested — <c>build</c>, <c>ui</c>, or <c>cli</c>.</summary>
    public string? IssuedVia { get; init; }

    /// <summary>Free-text note recorded at issue time (e.g. the project that asked).</summary>
    public string? Note { get; init; }

    /// <summary>True once an operator revoked it. Revoked entries are kept, never deleted.</summary>
    public bool Revoked { get; init; }

    /// <summary>When it was revoked, if it was.</summary>
    public DateTimeOffset? RevokedUtc { get; init; }

    /// <summary>Whether this credential should be accepted right now.</summary>
    public bool IsUsable => !Revoked && DateTimeOffset.UtcNow < NotAfter;
}

/// <summary>Whether the hub listens for remote clients, and where.</summary>
public sealed record RemoteSettings
{
    /// <summary>Whether the listener runs at all. Off until an operator turns it on.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The address to bind. Loopback by default — the SSH-tunnel shape, and the one that
    /// cannot be reached from anywhere else by accident.
    /// </summary>
    /// <remarks>
    /// A non-loopback or wildcard bind is permitted: mutual TLS is what protects the hub, not
    /// the network boundary, and an exposed port that requires a hub-issued client certificate
    /// is genuinely low-risk. It stays an explicit choice all the same, because "reachable
    /// from a public network" should never be something that happens by default.
    /// </remarks>
    public string BindAddress { get; init; } = "127.0.0.1";

    /// <summary>The TCP port to listen on.</summary>
    public int Port { get; init; } = RemoteEndpoint.DefaultPort;

    /// <summary>
    /// The address baked into issued credentials so a client knows where to dial. Null means
    /// the client must be told separately.
    /// </summary>
    public string? AdvertisedEndpoint { get; init; }
}

/// <summary>
/// The hub's remote state on disk: its certificate authority, its server certificate, the
/// credentials it has issued, and whether the listener is enabled.
/// </summary>
/// <remarks>
/// <para>
/// This is the hub's first persisted secret, so it follows the <see cref="KnownClientStore"/>
/// precedent exactly: a folder under <c>%APPDATA%\Keincheck</c>, atomic
/// write-temp-then-move so a crash mid-write cannot truncate the live file, and a corrupt
/// file starting empty rather than throwing.
/// </para>
/// <para>
/// <b>What protects the CA private key is the user-profile ACL, and nothing else.</b> The
/// PKCS#12 password stored alongside it in <c>settings.json</c> prevents empty-password
/// import quirks; it is not a second factor and is not pretending to be one. Anything already
/// running as this user can read the CA and mint credentials — but such code can already drive
/// every app through the control pipe, so this does not widen the boundary. It does mean a
/// compromised account can hand out durable remote access, which is why every issued
/// credential is listed and revocable.
/// </para>
/// </remarks>
public sealed class RemoteStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Dictionary<string, IssuedCredential> _issued = new(StringComparer.OrdinalIgnoreCase);

    private X509Certificate2? _ca;
    private X509Certificate2? _server;
    private string _pfxPassword = string.Empty;
    private RemoteSettings _settings = new();
    private int _disposed;

    private RemoteStore(string directory) => _directory = directory;

    /// <summary>The folder this store persists to.</summary>
    public string Directory => _directory;

    /// <summary>Whether a CA exists. Nothing can authenticate until it does.</summary>
    public bool IsProvisioned
    {
        get { lock (_gate) return _ca is not null && _server is not null; }
    }

    /// <summary>The current listener settings.</summary>
    public RemoteSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    /// <summary>The default location: <c>%APPDATA%\Keincheck\remote</c>.</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keincheck", "remote");

    /// <summary>
    /// Opens the store, loading whatever is already there. Never provisions on its own —
    /// creating a CA is an operator decision, so a hub that has never been asked to accept
    /// remote clients holds no key material at all.
    /// </summary>
    public static RemoteStore Open(string? directory = null)
    {
        var store = new RemoteStore(directory ?? DefaultDirectory());
        store.Load();
        return store;
    }

    // ---------------------------------------------------------------- provisioning

    /// <summary>
    /// Creates the CA and server certificate if they do not exist yet. Idempotent: calling it
    /// on an already-provisioned store is a no-op, so it never silently orphans the
    /// credentials already issued under the old root.
    /// </summary>
    /// <returns>True if it provisioned now, false if it was already provisioned.</returns>
    public bool Provision()
    {
        lock (_gate)
        {
            if (_ca is not null && _server is not null)
                return false;

            // A certificate authority on disk that this process could not LOAD is not the same
            // thing as no authority. Overwriting it would orphan every credential ever issued
            // — including ones baked into deployed builds — while issued.json still lists them
            // as usable, so clients would fail with an opaque TLS chain error rather than
            // anything that points at the cause. And the trigger is entirely plausible: a
            // corrupt or lost settings.json (which holds the key password), a backup or
            // anti-virus lock, an unavailable key container. The operator would see "not
            // listening", press Enable — the obvious move — and destroy the root.
            if (File.Exists(Path.Combine(_directory, "ca.pfx")))
            {
                throw new InvalidOperationException(
                    $"A certificate authority already exists in '{_directory}' but could not be " +
                    "loaded, so it will not be replaced — doing so would permanently invalidate " +
                    "every credential already issued from it. Restore the folder from backup, or " +
                    "delete ca.pfx, server.pfx and issued.json to start over and re-issue every " +
                    "client credential.");
            }

            _pfxPassword = RemoteCertificates.NewPkcs12Password();

            using var freshCa = RemoteCertificates.CreateCertificateAuthority(
                $"Keincheck Hub CA ({Environment.MachineName})");
            using var freshServer = RemoteCertificates.CreateServerCertificate(
                freshCa,
                Environment.MachineName,
                dnsNames: [Environment.MachineName, "localhost"],
                ipAddresses: [System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback]);

            WriteAllBytes("ca.pfx", RemoteCertificates.ExportPkcs12(freshCa, _pfxPassword));
            WriteAllBytes("server.pfx", RemoteCertificates.ExportPkcs12(freshServer, _pfxPassword));

            // Reload through the same path production uses on every start, so a provisioning
            // run and a restart cannot diverge in how the key is stored.
            _ca = RemoteCertificates.Load(ReadAllBytes("ca.pfx")!, _pfxPassword);
            _server = RemoteCertificates.Load(ReadAllBytes("server.pfx")!, _pfxPassword);

            SaveSettings_NoLock();
            return true;
        }
    }

    /// <summary>The CA, with its private key. Never leaves the hub.</summary>
    public X509Certificate2 CertificateAuthority
        => Get(_ca, "The hub has no certificate authority yet. Enable remote access first.");

    /// <summary>The hub's server certificate.</summary>
    public X509Certificate2 ServerCertificate
        => Get(_server, "The hub has no server certificate yet. Enable remote access first.");

    private X509Certificate2 Get(X509Certificate2? certificate, string message)
    {
        lock (_gate)
            return certificate ?? throw new InvalidOperationException(message);
    }

    // ---------------------------------------------------------------- issuance

    /// <summary>The longest credential lifetime the hub will grant.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(365);

    /// <summary>The default for a credential minted automatically by a build step.</summary>
    public static readonly TimeSpan BuildLifetime = TimeSpan.FromDays(90);

    /// <summary>The default for a credential an operator issues by hand.</summary>
    public static readonly TimeSpan ManualLifetime = TimeSpan.FromDays(365);

    /// <summary>
    /// Issues a client credential for <paramref name="host"/> and returns the bundle to hand
    /// over, plus the record kept for revocation.
    /// </summary>
    /// <remarks>
    /// The bundle carries a private key. A credential baked into a shipped binary is
    /// extractable from that binary — which is why lifetimes are clamped and every serial is
    /// recorded here rather than issued and forgotten.
    /// </remarks>
    public (string Bundle, IssuedCredential Record) Issue(
        string host, TimeSpan lifetime, string issuedVia, string? note = null)
    {
        RemoteCertificates.ValidateHostLabel(host);

        if (lifetime <= TimeSpan.Zero)
            lifetime = BuildLifetime;
        if (lifetime > MaxLifetime)
            lifetime = MaxLifetime;

        lock (_gate)
        {
            var ca = _ca ?? throw new InvalidOperationException(
                "The hub has no certificate authority yet. Enable remote access first.");

            using var client = RemoteCertificates.CreateClientCertificate(ca, host, lifetime);

            RemoteEndpoint? advertised = null;
            if (!string.IsNullOrWhiteSpace(_settings.AdvertisedEndpoint))
                RemoteEndpoint.TryParse(_settings.AdvertisedEndpoint, out advertised);
            advertised ??= new RemoteEndpoint { Host = _settings.BindAddress, Port = _settings.Port };

            var bundle = RemoteCredential.Serialize(client, ca, advertised);
            var record = new IssuedCredential
            {
                Serial = RemoteCertificates.SerialOf(client),
                Host = host,
                IssuedUtc = DateTimeOffset.UtcNow,
                NotAfter = client.NotAfter,
                IssuedVia = issuedVia,
                Note = note,
            };

            _issued[record.Serial] = record;
            SaveIssued_NoLock();
            return (bundle, record);
        }
    }

    /// <summary>Every credential ever issued, newest first. Revoked ones are retained.</summary>
    public IReadOnlyList<IssuedCredential> Issued()
    {
        lock (_gate)
            return _issued.Values.OrderByDescending(c => c.IssuedUtc).ToList();
    }

    /// <summary>Revokes a credential by serial. Returns false if the serial is unknown.</summary>
    public bool Revoke(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return false;

        lock (_gate)
        {
            if (!_issued.TryGetValue(serial, out var existing) || existing.Revoked)
                return false;

            _issued[serial] = existing with { Revoked = true, RevokedUtc = DateTimeOffset.UtcNow };
            SaveIssued_NoLock();
            return true;
        }
    }

    /// <summary>
    /// Whether a presented serial must be refused.
    /// </summary>
    /// <remarks>
    /// <b>Unknown serials are refused.</b> A validly-signed certificate this store has no
    /// record of means either the issued list was lost or someone got hold of the CA key;
    /// neither is a state in which to accept the session. Checked on every connect, so
    /// revocation takes effect on the next reconnect rather than at the next expiry.
    /// </remarks>
    public bool IsRevoked(string serial)
    {
        lock (_gate)
            return !_issued.TryGetValue(serial, out var record) || record.Revoked;
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Replaces the listener settings and persists them.</summary>
    public void UpdateSettings(RemoteSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings = settings;
            SaveSettings_NoLock();
        }
    }

    // ---------------------------------------------------------------- persistence

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                var settingsJson = ReadAllText("settings.json");
                if (settingsJson is not null)
                {
                    var persisted = JsonSerializer.Deserialize<PersistedSettings>(settingsJson, Json);
                    if (persisted is not null)
                    {
                        _settings = persisted.Settings ?? new RemoteSettings();
                        _pfxPassword = persisted.Pkcs12Password ?? string.Empty;
                    }
                }

                var issuedJson = ReadAllText("issued.json");
                if (issuedJson is not null)
                {
                    var list = JsonSerializer.Deserialize<List<IssuedCredential>>(issuedJson, Json);
                    foreach (var record in list ?? [])
                    {
                        if (!string.IsNullOrEmpty(record.Serial))
                            _issued[record.Serial] = record;
                    }
                }

                var caBytes = ReadAllBytes("ca.pfx");
                var serverBytes = ReadAllBytes("server.pfx");
                if (caBytes is not null && serverBytes is not null)
                {
                    _ca = RemoteCertificates.Load(caBytes, _pfxPassword);
                    _server = RemoteCertificates.Load(serverBytes, _pfxPassword);
                }
            }
            catch
            {
                // A corrupt or unreadable store starts empty rather than preventing the hub
                // from running. It reports as un-provisioned, so the listener stays off and
                // nothing can authenticate -- which is the correct failure direction.
                _ca?.Dispose();
                _server?.Dispose();
                _ca = null;
                _server = null;
            }
        }
    }

    private void SaveSettings_NoLock() => WriteAllText("settings.json",
        JsonSerializer.Serialize(new PersistedSettings
        {
            Settings = _settings,
            Pkcs12Password = _pfxPassword,
        }, Json));

    private void SaveIssued_NoLock() => WriteAllText("issued.json",
        JsonSerializer.Serialize(_issued.Values.OrderByDescending(c => c.IssuedUtc).ToList(), Json));

    private string? ReadAllText(string name)
    {
        var path = Path.Combine(_directory, name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private byte[]? ReadAllBytes(string name)
    {
        var path = Path.Combine(_directory, name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private void WriteAllText(string name, string content) => Write(name, tmp => File.WriteAllText(tmp, content));

    private void WriteAllBytes(string name, byte[] content) => Write(name, tmp => File.WriteAllBytes(tmp, content));

    /// <summary>Write to a sibling temp file then move, so a crash mid-write cannot truncate the live one.</summary>
    private void Write(string name, Action<string> write)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        var tmp = path + ".tmp";
        write(tmp);
        File.Move(tmp, path, overwrite: true);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_gate)
        {
            _ca?.Dispose();
            _server?.Dispose();
            _ca = null;
            _server = null;
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class PersistedSettings
    {
        public RemoteSettings? Settings { get; set; }

        /// <summary>
        /// The password on the PKCS#12 files beside this one. It is stored here on purpose and
        /// is <b>not</b> a security control — the user-profile ACL is. It exists only so the
        /// key files never rely on empty-password import behaviour.
        /// </summary>
        public string? Pkcs12Password { get; set; }
    }
}
