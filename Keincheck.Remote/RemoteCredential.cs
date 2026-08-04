using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keincheck.Remote;

/// <summary>
/// Everything a client needs to reach one hub: its own certificate and private key, the hub's
/// CA certificate to pin, and optionally the address to dial. Issued by the hub, delivered as
/// a single opaque string.
/// </summary>
/// <remarks>
/// <para>
/// One string, because a credential has to survive being pasted into a C# constant, dropped in
/// a file, or set as an environment variable — the three ways it actually reaches an app.
/// </para>
/// <para>
/// <b>Treat a bundle as a secret.</b> It carries a private key; whoever holds it can attach to
/// that hub as that host until the certificate expires or the operator revokes its serial.
/// A bundle baked into a shipped binary is extractable from that binary — which is why
/// build-issued credentials are short-lived and every serial is revocable.
/// </para>
/// <para>
/// <b>Load once and keep it.</b> TLS on Windows will not accept an ephemerally-keyed
/// certificate, so the certificates here occupy a key container until they are disposed.
/// Parsing a bundle per reconnect would accumulate one container per attempt on exactly the
/// flaky link that reconnects most.
/// </para>
/// </remarks>
public sealed class RemoteCredential : IDisposable
{
    /// <summary>Prefix identifying a v1 bundle, so a mistyped value fails clearly.</summary>
    public const string Prefix = "kcrem1_";

    /// <summary>Environment variable holding a bundle string directly.</summary>
    public const string BundleEnvironmentVariable = "KEINCHECK_REMOTE";

    /// <summary>Environment variable holding the path to a file containing a bundle.</summary>
    public const string FileEnvironmentVariable = "KEINCHECK_REMOTE_FILE";

    private int _disposed;

    private RemoteCredential(
        X509Certificate2 clientCertificate, X509Certificate2 certificateAuthority, RemoteEndpoint? endpoint)
    {
        ClientCertificate = clientCertificate;
        CertificateAuthority = certificateAuthority;
        DefaultEndpoint = endpoint;
        Host = RemoteCertificates.CommonNameOf(clientCertificate);
    }

    /// <summary>
    /// The host label this credential authenticates as — the client certificate's common name,
    /// and the <c>@Host</c> the hub will file the client under.
    /// </summary>
    public string Host { get; }

    /// <summary>The client certificate, with its private key.</summary>
    public X509Certificate2 ClientCertificate { get; }

    /// <summary>The hub's CA certificate (public only), the sole root this client will trust.</summary>
    public X509Certificate2 CertificateAuthority { get; }

    /// <summary>The address the hub suggested at issue time, if any.</summary>
    public RemoteEndpoint? DefaultEndpoint { get; }

    /// <summary>When the client certificate stops being accepted.</summary>
    public DateTimeOffset NotAfter => ClientCertificate.NotAfter;

    /// <summary>The client certificate's serial — the key an operator revokes by.</summary>
    public string Serial => RemoteCertificates.SerialOf(ClientCertificate);

    /// <summary>True once the certificate is within <paramref name="window"/> of expiry.</summary>
    public bool IsExpiringWithin(TimeSpan window) => DateTimeOffset.UtcNow + window >= NotAfter;

    // ---------------------------------------------------------------- serialize

    /// <summary>
    /// Packs an issued client certificate plus the hub's CA into a bundle string.
    /// </summary>
    public static string Serialize(
        X509Certificate2 clientCertificateWithKey,
        X509Certificate2 certificateAuthority,
        RemoteEndpoint? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(clientCertificateWithKey);
        ArgumentNullException.ThrowIfNull(certificateAuthority);
        if (!clientCertificateWithKey.HasPrivateKey)
            throw new ArgumentException(
                "A credential bundle must carry the client's private key.", nameof(clientCertificateWithKey));

        // The PKCS#12 password is random and travels inside the bundle. It is not a second
        // factor -- the bundle is the secret -- it only avoids the empty-password edge cases
        // some platforms have when importing a PFX.
        var password = RemoteCertificates.NewPkcs12Password();
        var payload = new BundlePayload
        {
            Version = 1,
            Host = RemoteCertificates.CommonNameOf(clientCertificateWithKey),
            ClientPkcs12 = Convert.ToBase64String(
                RemoteCertificates.ExportPkcs12(clientCertificateWithKey, password)),
            ClientPkcs12Password = password,
            CertificateAuthority = Convert.ToBase64String(certificateAuthority.Export(X509ContentType.Cert)),
            Endpoint = endpoint?.ToString(),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, BundleJson);
        return Prefix + Base64Url.Encode(json);
    }

    // ---------------------------------------------------------------- parse

    /// <summary>Parses a bundle string.</summary>
    /// <exception cref="FormatException">The bundle is malformed, truncated, or not a bundle.</exception>
    public static RemoteCredential Parse(string bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle))
            throw new FormatException("The remote credential is empty.");

        bundle = bundle.Trim();
        if (!bundle.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException(
                $"The remote credential does not start with '{Prefix}'. Re-issue it from the hub.");

        BundlePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BundlePayload>(
                Base64Url.Decode(bundle[Prefix.Length..]), BundleJson);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new FormatException("The remote credential is corrupt.", ex);
        }

        if (payload is null)
            throw new FormatException("The remote credential is empty.");
        if (payload.Version != 1)
            throw new FormatException(
                $"The remote credential is version {payload.Version}; this build understands version 1.");
        if (string.IsNullOrEmpty(payload.ClientPkcs12) || string.IsNullOrEmpty(payload.CertificateAuthority))
            throw new FormatException("The remote credential is missing its certificates.");

        X509Certificate2? client = null;
        X509Certificate2? ca = null;
        try
        {
            client = RemoteCertificates.Load(
                Convert.FromBase64String(payload.ClientPkcs12), payload.ClientPkcs12Password ?? string.Empty);
            ca = RemoteCertificates.LoadPublic(Convert.FromBase64String(payload.CertificateAuthority));

            RemoteEndpoint? endpoint = null;
            if (!string.IsNullOrWhiteSpace(payload.Endpoint))
                RemoteEndpoint.TryParse(payload.Endpoint, out endpoint);

            var credential = new RemoteCredential(client, ca, endpoint);
            client = null;
            ca = null;
            return credential;
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException("The remote credential's certificates could not be loaded.", ex);
        }
        finally
        {
            client?.Dispose();
            ca?.Dispose();
        }
    }

    /// <summary>Reads a bundle from a file.</summary>
    public static RemoteCredential LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Resolves a credential from the environment, or <c>null</c> when none is configured.
    /// <see cref="FileEnvironmentVariable"/> wins over <see cref="BundleEnvironmentVariable"/>,
    /// because a path is the safer way to supply one and should not be silently overridden.
    /// </summary>
    /// <exception cref="FormatException">A variable was set but its contents are unusable.</exception>
    public static RemoteCredential? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(FileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                throw new FormatException($"{FileEnvironmentVariable} points at '{path}', which does not exist.");
            return LoadFile(path);
        }

        var inline = Environment.GetEnvironmentVariable(BundleEnvironmentVariable);
        return string.IsNullOrWhiteSpace(inline) ? null : Parse(inline);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        ClientCertificate.Dispose();
        CertificateAuthority.Dispose();
    }

    private static readonly JsonSerializerOptions BundleJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class BundlePayload
    {
        [JsonPropertyName("v")] public int Version { get; set; }
        [JsonPropertyName("host")] public string? Host { get; set; }
        [JsonPropertyName("clientPkcs12")] public string? ClientPkcs12 { get; set; }
        [JsonPropertyName("clientPkcs12Password")] public string? ClientPkcs12Password { get; set; }
        [JsonPropertyName("ca")] public string? CertificateAuthority { get; set; }
        [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    }

    /// <summary>
    /// Base64url without padding, so a bundle survives being pasted into a URL, a shell, or a
    /// C# string literal without escaping.
    /// </summary>
    private static class Base64Url
    {
        public static string Encode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string text)
        {
            var s = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
        }
    }
}
