using System.Security.Cryptography.X509Certificates;
using Keincheck.Remote;
using Xunit;

namespace Keincheck.Remote.Tests;

/// <summary>
/// The PKCS#12 storage flags used to load a credential.
///
/// These assertions exist because the rule they pin can only be *observed* on platforms this
/// suite will never run on. iOS, tvOS and Mac Catalyst throw
/// <see cref="PlatformNotSupportedException"/> for <c>Exportable</c>, so a Mac Catalyst app
/// referencing this package used to fail on every credential load; Windows SChannel needs
/// <c>UserKeySet</c> and rejects <c>EphemeralKeySet</c>. Getting either wrong breaks a whole
/// platform silently at run time, so the decision is a pure function and tested as one.
/// </summary>
public sealed class CertificateLoadFlagsTests
{
    [Fact]
    public void Apple_Mobile_Never_Asks_For_Exportable()
    {
        var flags = RemoteCertificates.LoadFlagsFor(appleMobile: true);

        // The flag that throws PlatformNotSupportedException on iOS/tvOS/MacCatalyst.
        Assert.False(flags.HasFlag(X509KeyStorageFlags.Exportable));
        // Rejected on those platforms too, and never wanted anywhere.
        Assert.False(flags.HasFlag(X509KeyStorageFlags.PersistKeySet));
    }

    [Fact]
    public void Desktop_Keeps_Exportable()
    {
        var flags = RemoteCertificates.LoadFlagsFor(appleMobile: false);

        Assert.True(flags.HasFlag(X509KeyStorageFlags.Exportable));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_Platform_Uses_UserKeySet_And_Never_Ephemeral(bool appleMobile)
    {
        var flags = RemoteCertificates.LoadFlagsFor(appleMobile);

        // UserKeySet is what Windows SChannel needs; it is silently ignored elsewhere, so it
        // is safe to assert unconditionally.
        Assert.True(flags.HasFlag(X509KeyStorageFlags.UserKeySet));

        // EphemeralKeySet breaks TLS on Windows for both server and client auth, and macOS
        // cannot load a private key without writing a keychain, so it refuses it outright.
        Assert.False(flags.HasFlag(X509KeyStorageFlags.EphemeralKeySet));
    }

    [Fact]
    public void A_Credential_Still_Round_Trips_Under_The_Non_Exportable_Flags()
    {
        // The behavioural half: proves dropping Exportable does not break loading or the
        // private key that TLS then needs. Runs the real load path with the mobile flag set.
        using var ca = RemoteCertificates.CreateCertificateAuthority("test-ca");
        using var leaf = RemoteCertificates.CreateClientCertificate(ca, "somehost", TimeSpan.FromDays(30));

        var password = RemoteCertificates.NewPkcs12Password();
        var pkcs12 = RemoteCertificates.ExportPkcs12(leaf, password);

#pragma warning disable SYSLIB0057 // matches RemoteCertificates.Load; this assembly is net8.0.
        using var reloaded = new X509Certificate2(
            pkcs12, password, RemoteCertificates.LoadFlagsFor(appleMobile: true));
#pragma warning restore SYSLIB0057

        Assert.True(reloaded.HasPrivateKey);
        Assert.Equal(leaf.Thumbprint, reloaded.Thumbprint);
    }
}
