using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Keincheck.Remote;

/// <summary>
/// Creates and loads the certificates the remote transport authenticates with: a hub-owned
/// CA, the hub's server leaf, and one client leaf per remote target.
/// </summary>
/// <remarks>
/// <para>
/// Every platform quirk this design depends on is concentrated here, because each was
/// established empirically against SChannel rather than read off a doc page:
/// </para>
/// <list type="bullet">
///   <item><b>Ephemeral keys do not work.</b> Loading a certificate with
///   <see cref="X509KeyStorageFlags.EphemeralKeySet"/> fails TLS on Windows for
///   <i>both</i> server auth (the handshake dies with an unexpected EOF) and client auth
///   ("the platform does not support ephemeral keys"). <see cref="Load"/> therefore uses
///   <see cref="X509KeyStorageFlags.UserKeySet"/>, which writes a key container that is
///   released when the certificate is disposed — so callers must load a credential
///   <b>once</b> and reuse it, never per reconnect.</item>
///   <item><b>A leaf cannot predate its issuer.</b>
///   <c>CertificateRequest.Create(issuer, notBefore, ...)</c> throws
///   <see cref="ArgumentException"/> if <c>notBefore</c> is earlier than the issuer's, so
///   the CA is backdated further than any leaf it will ever sign.</item>
///   <item><b>EKUs are load-bearing, not decorative.</b> Client and server certificates get
///   strictly separate extended key usages. Every client certificate is signed by the same
///   CA that clients pin, so if a client certificate were also usable for server auth, any
///   enrolled client could impersonate the hub to any other and harvest its screenshots.
///   <see cref="RemoteTls"/> enforces the distinction explicitly — see the note there.</item>
/// </list>
/// </remarks>
public static class RemoteCertificates
{
    /// <summary>OID for TLS server authentication.</summary>
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>OID for TLS client authentication.</summary>
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>How long a freshly-created CA is valid.</summary>
    public static readonly TimeSpan CaLifetime = TimeSpan.FromDays(365 * 10);

    /// <summary>How long a hub server certificate is valid.</summary>
    public static readonly TimeSpan ServerLifetime = TimeSpan.FromDays(365 * 2);

    /// <summary>
    /// How far the CA is backdated relative to "now", so it can always sign a leaf that is
    /// itself backdated by <see cref="ClockSkew"/> for tolerance.
    /// </summary>
    private static readonly TimeSpan CaBackdate = TimeSpan.FromDays(1);

    /// <summary>Backdating applied to leaves so a modest clock difference does not reject them.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The storage flags TLS actually accepts on Windows. Not <c>EphemeralKeySet</c> (fails
    /// outright) and not <c>PersistKeySet</c> (which would leave the key container behind
    /// after disposal, accumulating one per load).
    /// </summary>
    private const X509KeyStorageFlags LoadFlags =
        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable;

    // ---------------------------------------------------------------- creation

    /// <summary>
    /// Creates the hub's self-signed CA. This is the single root of trust: the hub signs
    /// every client and server certificate with it, and clients pin it.
    /// </summary>
    public static X509Certificate2 CreateCertificateAuthority(string commonName, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={Escape(commonName)}"), key, HashAlgorithmName.SHA256);

        // pathLength 0: the CA may issue leaves but no intermediate CAs.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var start = (now ?? DateTimeOffset.UtcNow) - CaBackdate;
        return request.CreateSelfSigned(start, start + CaBackdate + CaLifetime);
    }

    /// <summary>
    /// Issues the hub's server certificate. <paramref name="dnsNames"/> and
    /// <paramref name="ipAddresses"/> populate the SAN so a conventional client could do
    /// hostname validation; Keincheck's own clients do not rely on it, because over an SSH
    /// tunnel the address is always loopback and identity comes from the CA instead.
    /// </summary>
    public static X509Certificate2 CreateServerCertificate(
        X509Certificate2 certificateAuthority,
        string commonName,
        IEnumerable<string>? dnsNames = null,
        IEnumerable<System.Net.IPAddress>? ipAddresses = null,
        DateTimeOffset? now = null)
    {
        var san = new SubjectAlternativeNameBuilder();
        var any = false;
        foreach (var dns in dnsNames ?? [])
        {
            if (string.IsNullOrWhiteSpace(dns)) continue;
            san.AddDnsName(dns);
            any = true;
        }
        foreach (var ip in ipAddresses ?? [])
        {
            san.AddIpAddress(ip);
            any = true;
        }
        if (!any)
            san.AddDnsName(commonName);

        return CreateLeaf(certificateAuthority, commonName, ServerAuthOid, ServerLifetime, san, now);
    }

    /// <summary>
    /// Issues a client certificate for one remote target. <paramref name="commonName"/>
    /// becomes the authoritative host label: it is what the hub stamps onto the session and
    /// what appears in the client's hub id as <c>AppId@Host#n</c>.
    /// </summary>
    public static X509Certificate2 CreateClientCertificate(
        X509Certificate2 certificateAuthority,
        string commonName,
        TimeSpan lifetime,
        DateTimeOffset? now = null)
    {
        ValidateHostLabel(commonName);
        return CreateLeaf(certificateAuthority, commonName, ClientAuthOid, lifetime, san: null, now);
    }

    /// <summary>The longest host label a client certificate may carry.</summary>
    public const int MaxHostLabelLength = 64;

    /// <summary>
    /// Rejects a host label that is not safe to use as an identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Restricting the character set rather than escaping it closes two separate problems at
    /// once, which is why it is a hard rule at issue time rather than a sanitiser later.
    /// </para>
    /// <para>
    /// The first is directory-name injection: the label becomes the certificate's common name,
    /// where <c>,</c> and <c>=</c> are structural, so an unrestricted label could smuggle extra
    /// relative distinguished names into the subject.
    /// </para>
    /// <para>
    /// The second is the hub's own identifier scheme. A remote client is filed as
    /// <c>AppId@Host#n</c>, and the hub finds the instance suffix by searching for the last
    /// <c>#</c>. A label containing <c>@</c> or <c>#</c> would make that ambiguous — and the
    /// code path it corrupts is the one that decides whether an id refers to something the hub
    /// may launch locally, so ambiguity there is not a cosmetic problem.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The label is empty, too long, or has disallowed characters.</exception>
    public static void ValidateHostLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        if (label.Length > MaxHostLabelLength)
            throw new ArgumentException(
                $"A host label may be at most {MaxHostLabelLength} characters; '{label}' is {label.Length}.",
                nameof(label));

        foreach (var c in label)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_';
            if (!ok)
                throw new ArgumentException(
                    $"A host label may only contain letters, digits, '.', '-' and '_'; '{label}' contains '{c}'.",
                    nameof(label));
        }

        if (label[0] is '.' or '-' || label[^1] is '.' or '-')
            throw new ArgumentException(
                $"A host label may not start or end with '.' or '-'; got '{label}'.", nameof(label));
    }

    /// <summary>
    /// Coerces arbitrary text into a valid host label, for defaults derived from a machine
    /// name. Callers taking operator input should prefer <see cref="ValidateHostLabel"/> and
    /// report the problem rather than silently altering what was asked for.
    /// </summary>
    public static string SanitizeHostLabel(string? label, string fallback = "remote")
    {
        if (string.IsNullOrWhiteSpace(label))
            return fallback;

        var sb = new System.Text.StringBuilder(Math.Min(label.Length, MaxHostLabelLength));
        foreach (var c in label)
        {
            if (sb.Length >= MaxHostLabelLength)
                break;
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_';
            sb.Append(ok ? c : '_');
        }

        var result = sb.ToString().Trim('.', '-');
        return result.Length == 0 ? fallback : result;
    }

    private static X509Certificate2 CreateLeaf(
        X509Certificate2 issuer,
        string commonName,
        string ekuOid,
        TimeSpan lifetime,
        SubjectAlternativeNameBuilder? san,
        DateTimeOffset? now)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "A certificate lifetime must be positive.");
        if (!issuer.HasPrivateKey)
            throw new ArgumentException("The issuing certificate has no private key.", nameof(issuer));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={Escape(commonName)}"), key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        // Marked critical so a peer that cannot understand the EKU must reject rather than ignore it.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(ekuOid) }, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (san is not null)
            request.CertificateExtensions.Add(san.Build());

        // Clamp the start FIRST, then derive the end from the clamped value. Doing it the
        // other way round lets a start that gets pushed forward end up after its own end,
        // which CertificateRequest.Create rejects with an unhelpful ArgumentException.
        var issued = now ?? DateTimeOffset.UtcNow;
        var start = issued - ClockSkew;
        if (start < issuer.NotBefore)
            start = issuer.NotBefore;

        // Never outlive the issuer: a leaf valid past its CA is unusable and confusing.
        var end = start + ClockSkew + lifetime;
        if (end > issuer.NotAfter)
            end = issuer.NotAfter;

        if (end <= start)
        {
            throw new ArgumentException(
                $"The issuing CA is valid until {issuer.NotAfter:u}, which leaves no room for a " +
                $"certificate starting {start:u}. Renew the CA before issuing.", nameof(issuer));
        }

        using var signed = request.Create(issuer, start, end, NewSerialNumber());
        return signed.CopyWithPrivateKey(key);
    }

    /// <summary>A 16-byte positive serial. Uniqueness matters: it is the revocation key.</summary>
    private static byte[] NewSerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // keep it positive so the encoded form has no leading pad byte
        return serial;
    }

    /// <summary>
    /// Escapes the RFC 4514 special characters so a hostile or merely awkward target name
    /// cannot inject extra RDNs into the subject and claim a different identity.
    /// </summary>
    private static string Escape(string value)
    {
        Span<char> special = [',', '+', '"', '\\', '<', '>', ';', '='];
        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (special.Contains(c))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- persistence

    /// <summary>Exports a certificate and its private key as password-protected PKCS#12.</summary>
    public static byte[] ExportPkcs12(X509Certificate2 certificate, string password)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Export(X509ContentType.Pkcs12, password);
    }

    /// <summary>
    /// Loads a PKCS#12 blob into a certificate usable for TLS.
    /// </summary>
    /// <remarks>
    /// <b>Dispose the result</b>, and load it once rather than per connection: the flags
    /// required for SChannel write a key container that is only released on disposal.
    /// </remarks>
    public static X509Certificate2 Load(byte[] pkcs12, string password)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);
#pragma warning disable SYSLIB0057 // X509CertificateLoader is net9+; this assembly targets net8.0.
        return new X509Certificate2(pkcs12, password, LoadFlags);
#pragma warning restore SYSLIB0057
    }

    /// <summary>Loads a public-only certificate (no private key) from DER bytes.</summary>
    public static X509Certificate2 LoadPublic(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);
#pragma warning disable SYSLIB0057
        return new X509Certificate2(der);
#pragma warning restore SYSLIB0057
    }

    /// <summary>A random password for a PKCS#12 blob whose container is itself the secret.</summary>
    public static string NewPkcs12Password() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The certificate's common name — for a client certificate, the authoritative host label.
    /// </summary>
    public static string CommonNameOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? string.Empty;
    }

    /// <summary>The certificate serial as uppercase hex — the stable key for revocation.</summary>
    public static string SerialOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.SerialNumber.ToUpperInvariant();
    }
}
