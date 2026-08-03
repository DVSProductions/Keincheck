using System.Security.Cryptography.X509Certificates;
using Keincheck.Remote;

namespace Keincheck.Remote.Tests;

/// <summary>
/// A disposable throwaway PKI for tests: one CA, its server certificate, and however many
/// client certificates a test needs.
/// </summary>
/// <remarks>
/// Everything is disposed at the end of a test because the flags TLS requires on Windows
/// (<c>UserKeySet</c>, since ephemeral keys are refused outright) allocate a key container per
/// loaded certificate that is only released on disposal. A leaky test suite would slowly fill
/// the developer's key store.
/// </remarks>
internal sealed class TestPki : IDisposable
{
    private readonly List<X509Certificate2> _owned = [];

    public TestPki(string name = "Test Hub CA")
    {
        Ca = Track(RemoteCertificates.CreateCertificateAuthority(name));
        // Round-trip through PKCS#12 exactly as the hub does when it reloads from disk, so the
        // tests exercise the same storage-flag path production uses.
        Server = Track(Reload(RemoteCertificates.CreateServerCertificate(
            Ca, "test-hub", dnsNames: ["localhost"], ipAddresses: [System.Net.IPAddress.Loopback])));
    }

    /// <summary>The root every peer in this PKI is validated against.</summary>
    public X509Certificate2 Ca { get; }

    /// <summary>The hub's server certificate.</summary>
    public X509Certificate2 Server { get; }

    /// <summary>The CA as a public-only certificate, which is what a client actually pins.</summary>
    public X509Certificate2 CaPublic() => Track(RemoteCertificates.LoadPublic(Ca.Export(X509ContentType.Cert)));

    /// <summary>Issues a client certificate for <paramref name="host"/>.</summary>
    public X509Certificate2 Client(string host, TimeSpan? lifetime = null)
        => Track(Reload(RemoteCertificates.CreateClientCertificate(
            Ca, host, lifetime ?? TimeSpan.FromDays(90))));

    /// <summary>Issues a certificate with server-auth EKU but a client-ish name, for EKU tests.</summary>
    public X509Certificate2 ServerLeaf(string host)
        => Track(Reload(RemoteCertificates.CreateServerCertificate(Ca, host)));

    /// <summary>A credential bundle for <paramref name="host"/>, as the hub would issue one.</summary>
    public RemoteCredential Credential(string host, RemoteEndpoint? endpoint = null, TimeSpan? lifetime = null)
        => RemoteCredential.Parse(RemoteCredential.Serialize(Client(host, lifetime), Ca, endpoint));

    /// <summary>Simulates persisting and reloading, which is what production always does.</summary>
    private X509Certificate2 Reload(X509Certificate2 certificate)
    {
        var password = RemoteCertificates.NewPkcs12Password();
        using (certificate)
            return RemoteCertificates.Load(RemoteCertificates.ExportPkcs12(certificate, password), password);
    }

    private X509Certificate2 Track(X509Certificate2 certificate)
    {
        _owned.Add(certificate);
        return certificate;
    }

    public void Dispose()
    {
        foreach (var certificate in _owned)
        {
            try { certificate.Dispose(); } catch { /* best effort */ }
        }
        _owned.Clear();
    }
}
