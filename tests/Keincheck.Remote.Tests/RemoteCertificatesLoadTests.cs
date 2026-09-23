using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Keincheck.Remote;
using Xunit;

namespace Keincheck.Remote.Tests;

/// <summary>
/// <see cref="RemoteCertificates.Load"/> must never return a certificate whose private key
/// cannot actually be opened.
/// </summary>
/// <remarks>
/// On Windows the PKCS#12 import persists the key into a CNG container that the certificate
/// then refers to by name. Windows 11 (build 26200) intermittently reports success without
/// having written that container, so the very first use of the key -- signing a leaf,
/// exporting, or the TLS handshake -- fails with NTE_BAD_KEYSET ("Keyset does not exist").
/// The loader therefore proves the key is reachable before handing the certificate out, and
/// imports again when it is not. These tests drive that logic through a fake importer so the
/// platform flake does not have to be provoked, plus one check that the fake really reproduces
/// the production symptom.
/// </remarks>
// Fabricating the failure needs CNG (a key container to delete), so the class is declared
// Windows-only for the platform analyzer; each test that needs it also returns early elsewhere,
// which keeps the file compiling and the suite green on a non-Windows build.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RemoteCertificatesLoadTests
{
    private const string Password = "test";

    [Fact]
    public void A_Certificate_Whose_Container_Was_Never_Written_Is_Detected_And_Replaced()
    {
        if (!OperatingSystem.IsWindows())
            return; // key containers are a CNG concept; nothing to reproduce elsewhere

        var pfx = ClientPfx();
        var attempts = 0;
        X509Certificate2? dead = null;

        using var loaded = RemoteCertificates.LoadVerified(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                dead = WithVanishedKeyContainer(RemoteCertificates.Load(pfx, Password));
                return dead;
            }
            return RemoteCertificates.Load(pfx, Password);
        });

        Assert.Equal(2, attempts);
        Assert.True(CanSign(loaded), "the certificate handed out must have a usable private key");
        Assert.NotNull(dead);
        Assert.Throws<CryptographicException>(() => dead!.RawData); // the rejected instance was disposed
    }

    [Fact]
    public void Loading_Gives_Up_After_A_Bounded_Number_Of_Attempts_And_Reports_The_Platform_Error()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var pfx = ClientPfx();
        var handedOut = new List<X509Certificate2>();

        var ex = Assert.Throws<CryptographicException>(() => RemoteCertificates.LoadVerified(() =>
        {
            var certificate = WithVanishedKeyContainer(RemoteCertificates.Load(pfx, Password));
            handedOut.Add(certificate);
            return certificate;
        }, attempts: 3));

        Assert.Equal(3, handedOut.Count);
        Assert.All(handedOut, c => Assert.Throws<CryptographicException>(() => c.RawData));
        Assert.NotNull(ex.InnerException);
        Assert.Contains("private key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Good_Import_Is_Handed_Out_On_The_First_Attempt()
    {
        var pfx = ClientPfx();
        var attempts = 0;

        using var loaded = RemoteCertificates.LoadVerified(() =>
        {
            attempts++;
            return RemoteCertificates.Load(pfx, Password);
        });

        Assert.Equal(1, attempts);
        Assert.True(CanSign(loaded));
    }

    [Fact]
    public void The_Simulated_Failure_Is_The_Real_Symptom()
    {
        // Guards the tests above: if deleting the container behind a loaded certificate did not
        // produce NTE_BAD_KEYSET on use, the fake importer would be testing nothing.
        if (!OperatingSystem.IsWindows())
            return;

        using var dead = WithVanishedKeyContainer(RemoteCertificates.Load(ClientPfx(), Password));

        Assert.True(dead.HasPrivateKey, "the certificate still claims a key -- that is what makes the flake silent");
        var ex = Assert.ThrowsAny<CryptographicException>(() =>
        {
            using var key = dead.GetECDsaPrivateKey();
            key!.SignData(new byte[] { 1 }, HashAlgorithmName.SHA256);
        });
        Assert.Equal(unchecked((int)0x80090016), ex.HResult); // NTE_BAD_KEYSET
    }

    [Fact]
    public void Load_Still_Refuses_A_Wrong_Password_Rather_Than_Retrying_Forever()
    {
        var pfx = ClientPfx();
        Assert.ThrowsAny<CryptographicException>(() => RemoteCertificates.Load(pfx, "not the password"));
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] ClientPfx()
    {
        using var ca = RemoteCertificates.CreateCertificateAuthority("Load Test CA");
        using var client = RemoteCertificates.CreateClientCertificate(ca, "host", TimeSpan.FromDays(1));
        return RemoteCertificates.ExportPkcs12(client, Password);
    }

    private static bool CanSign(X509Certificate2 certificate)
    {
        using var key = certificate.GetECDsaPrivateKey();
        if (key is null) return false;
        key.SignData(new byte[] { 1 }, HashAlgorithmName.SHA256);
        return true;
    }

    /// <summary>
    /// Turns a healthy loaded certificate into what the platform flake produces: key-provider
    /// info naming a container that does not exist.
    /// </summary>
    private static X509Certificate2 WithVanishedKeyContainer(X509Certificate2 certificate)
    {
        string name;
        using (var key = (ECDsaCng)certificate.GetECDsaPrivateKey()!)
            name = key.Key.KeyName!;

        using (var container = CngKey.Open(name))
            container.Delete();

        return certificate;
    }
}
