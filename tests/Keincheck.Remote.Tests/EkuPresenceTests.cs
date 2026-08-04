using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Keincheck.Remote;
using Xunit;

namespace Keincheck.Remote.Tests;

/// <summary>
/// Role separation rests on every leaf declaring an EKU. These pin the case where one does not.
/// </summary>
/// <remarks>
/// <para>
/// <c>X509ChainPolicy.ApplicationPolicy</c> implements RFC 5280, where an <b>absent</b>
/// extended-key-usage extension means "valid for all purposes". Chain-building alone therefore
/// enforces the weaker property "EKU compatible" rather than the "EKU present and correct" the
/// validation gate documents — and absent is compatible with everything, in both directions at
/// once. The existing confused-deputy tests cannot see this: they mint through
/// <c>RemoteCertificates.CreateLeaf</c>, which always attaches an EKU, so no fixture they can
/// build is capable of expressing the case.
/// </para>
/// <para>
/// Not reachable through the shipped library today — the hub has no CSR-signing path, so forging
/// one needs the CA private key. It is pinned because it is the load-bearing assumption
/// underneath every other role check, and because the next leaf factory someone adds is exactly
/// where it would quietly stop holding.
/// </para>
/// </remarks>
public sealed class EkuPresenceTests
{
    /// <summary>
    /// Signs a leaf under <paramref name="ca"/> with <b>no</b> EKU extension at all — the shape
    /// the library never produces and the gate must still refuse.
    /// </summary>
    private static X509Certificate2 EkuLessLeaf(X509Certificate2 ca, string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}"), key, HashAlgorithmName.SHA256);

        // Everything a normal leaf has EXCEPT the enhanced-key-usage extension.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        var now = DateTimeOffset.UtcNow;
        using var signed = request.Create(
            ca, now.AddMinutes(-5), now.AddDays(30), Guid.NewGuid().ToByteArray());

        // Re-attach the private key and round-trip through PKCS#12, exactly as production loads.
        using var withKey = signed.CopyWithPrivateKey(key);
        var password = RemoteCertificates.NewPkcs12Password();
        return RemoteCertificates.Load(RemoteCertificates.ExportPkcs12(withKey, password), password);
    }

    [Theory]
    [InlineData(RemoteCertificates.ClientAuthOid)]
    [InlineData(RemoteCertificates.ServerAuthOid)]
    public void A_Leaf_With_No_EKU_Is_Refused_For_Either_Role(string requiredEku)
    {
        // One certificate, both roles, both refused. Under chain-policy checking alone this
        // single leaf satisfied BOTH of these calls — which is precisely the confused deputy the
        // EKU split exists to prevent, reachable with one certificate instead of two.
        using var pki = new TestPki();
        using var leaf = EkuLessLeaf(pki.Ca, "no-eku");

        var validated = RemoteTls.Validate(
            leaf, SslPolicyErrors.None, pki.Ca, requiredEku);

        Assert.Null(validated);
    }

    [Fact]
    public void A_Correctly_Scoped_Leaf_Is_Still_Accepted()
    {
        // The guard must reject only the absent case. A real client certificate presented for
        // the client role has to keep working, or this is a self-inflicted outage rather than a
        // hardening.
        using var pki = new TestPki();
        var client = pki.Client("MACHINENAME");

        using var validated = RemoteTls.Validate(
            client, SslPolicyErrors.None, pki.Ca, RemoteCertificates.ClientAuthOid);

        Assert.NotNull(validated);
        Assert.Equal("MACHINENAME", RemoteCertificates.CommonNameOf(validated!));
    }

    [Fact]
    public void A_Leaf_Presented_For_The_Wrong_Role_Is_Still_Refused()
    {
        // The pre-existing property, asserted here directly on the gate rather than through a
        // handshake, so a failure points at the check rather than at "TLS didn't connect".
        using var pki = new TestPki();
        var client = pki.Client("MACHINENAME");

        Assert.Null(RemoteTls.Validate(
            client, SslPolicyErrors.None, pki.Ca, RemoteCertificates.ServerAuthOid));
    }

    [Fact]
    public async Task An_EkuLess_Leaf_Cannot_Authenticate_Over_A_Real_Handshake()
    {
        // The gate is only interesting if it is the one SChannel actually consults. Prove the
        // refusal survives a real socket rather than only a direct call.
        using var pki = new TestPki();
        using var leaf = EkuLessLeaf(pki.Ca, "no-eku-client");
        using var credential = RemoteCredential.Parse(RemoteCredential.Serialize(leaf, pki.Ca));

        var listener = StreamTransport.CreateListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            var accept = Task.Run(async () =>
            {
                var (channel, peer, _) = await StreamTransport.AcceptAsync(
                    listener, pki.Server, pki.Ca, cts.Token);
                await channel.DisposeAsync();
                peer.Dispose();
            }, cts.Token);

            var endpoint = new RemoteEndpoint { Host = "127.0.0.1", Port = port };

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var channel = await StreamTransport.ConnectAsync(
                    endpoint, credential, TimeSpan.FromSeconds(10), cts.Token);
                // A refused client certificate can surface either at the handshake or on the
                // first read, depending on when SChannel reports the alert, so force a read.
                await channel.ReceiveAsync(cts.Token);
            });

            await Assert.ThrowsAnyAsync<Exception>(() => accept);
        }
        finally
        {
            listener.Stop();
        }
    }
}
