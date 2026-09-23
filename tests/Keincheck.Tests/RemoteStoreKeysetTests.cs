using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Keincheck.Hub.Remote;
using Keincheck.Remote;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Reproduction of the "Keyset does not exist" failure (NTE_BAD_KEYSET, 0x80090016; in German
/// "Der Schluesselsatz ist nicht vorhanden") that made every <c>hub_remote_issue</c> fail on
/// a freshly enabled hub.
/// </summary>
/// <remarks>
/// <para>
/// On Windows 11 (observed on build 26200) <c>PFXImportCertStore</c> -- what the
/// <see cref="X509Certificate2"/> PKCS#12 constructor calls with <c>UserKeySet</c> --
/// intermittently returns a certificate whose key-provider info names a key container that was
/// never written: no key file appears, <c>CngKey.Exists</c> says false, and the condition is
/// permanent for that instance while re-importing the same bytes works. Measured at roughly
/// 0.1-0.5% of imports, arriving in bursts spread over wall time rather than per import. The hub
/// imports its CA exactly once and keeps it for the life of the process, so one unlucky import
/// turns into "every credential fails until the hub restarts" -- which is exactly what was seen.
/// </para>
/// <para>
/// A single Provision-then-Issue therefore only fails by chance, and because the failures come
/// per unit of wall time this four-second run catches one only now and then: on the affected
/// machine the unfixed loader failed 3 of 15 runs. It is a smoke test of the real path, not the
/// regression proof; that is RemoteCertificatesLoadTests, which fabricates the failure on demand.
/// The fixed loader (which verifies the key is reachable before handing the certificate out and
/// re-imports otherwise) passes deterministically.
/// </para>
/// </remarks>
public sealed class RemoteStoreKeysetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kc-keyset-{Guid.NewGuid():N}");

    private const int Workers = 8;
    private const int RoundsPerWorker = 30;
    private const int CredentialsPerRound = 3;

    [Fact]
    public void Provisioning_And_Issuing_Never_Hands_Out_A_Certificate_Whose_Key_Does_Not_Exist()
    {
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers }, worker =>
        {
            for (var round = 0; round < RoundsPerWorker; round++)
            {
                var label = $"worker {worker} round {round}";
                try
                {
                    using var store = RemoteStore.Open(Path.Combine(_dir, $"hub-{worker}-{round}"));
                    Assert.True(store.Provision(), "a fresh directory must provision");

                    // The listener hands this to SChannel; a dead key there surfaces later as an
                    // unexpected EOF on every client's handshake.
                    AssertSigns(store.ServerCertificate, $"{label}: server certificate");

                    for (var i = 0; i < CredentialsPerRound; i++)
                    {
                        // Issue signs with the CA: a dead CA key throws NTE_BAD_KEYSET right here.
                        var (bundle, _) = store.Issue($"target-{i}", TimeSpan.FromDays(1), issuedVia: "test");

                        // And what the client will load from the bundle must be usable too, or
                        // its handshake dies with "the credentials supplied were not recognized".
                        using var credential = RemoteCredential.Parse(bundle);
                        AssertSigns(credential.ClientCertificate, $"{label}: issued credential {i}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{label}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        Assert.True(failures.IsEmpty,
            $"{failures.Count} of {Workers * RoundsPerWorker} rounds hit an unusable private key:\n"
            + string.Join("\n", failures.Take(10)));
    }

    private static void AssertSigns(X509Certificate2 certificate, string what)
    {
        using var key = certificate.GetECDsaPrivateKey();
        Assert.NotNull(key);
        try
        {
            key!.SignData(new byte[] { 1 }, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"{what} has a private key that cannot be used: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
