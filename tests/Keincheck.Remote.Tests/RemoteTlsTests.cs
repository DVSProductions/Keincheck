using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Keincheck.Protocol;
using Keincheck.Remote;
using Xunit;

namespace Keincheck.Remote.Tests;

/// <summary>
/// End-to-end tests of the mutually-authenticated TLS transport over a real loopback socket.
/// </summary>
/// <remarks>
/// These are deliberately not mocked. The properties being asserted — that a certificate from
/// another CA is refused, that a client certificate cannot serve as a server certificate — are
/// properties of SChannel's behaviour under a custom validation callback, and a mock would
/// only assert that the test's own idea of TLS is self-consistent.
/// </remarks>
public sealed class RemoteTlsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs one full connect against a one-shot listener and returns the host the server
    /// derived from the validated client certificate.
    /// </summary>
    private static async Task<string> ConnectAsync(
        X509Certificate2 serverCertificate,
        X509Certificate2 serverTrustsCa,
        RemoteCredential clientCredential)
    {
        var listener = StreamTransport.CreateListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(Budget);

        var serverTask = Task.Run(async () =>
        {
            var (channel, peer, _) = await StreamTransport.AcceptAsync(
                listener, serverCertificate, serverTrustsCa, cts.Token);
            await using (channel)
            using (peer)
            {
                var host = RemoteCertificates.CommonNameOf(peer);
                var welcome = await RemoteHandshake.ServerAsync(channel, host, "test", cancellationToken: cts.Token);
                Assert.NotNull(welcome);
                return host;
            }
        }, cts.Token);

        try
        {
            var endpoint = new RemoteEndpoint { Host = "127.0.0.1", Port = port };
            var channel = await StreamTransport.ConnectAsync(
                endpoint, clientCredential, TimeSpan.FromSeconds(10), cts.Token);
            await using (channel)
            {
                await RemoteHandshake.ClientAsync(channel, "test-client", cts.Token);
            }
            return await serverTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task Enrolled_Client_Connects_And_The_Hub_Learns_Its_Host_From_The_Certificate()
    {
        using var pki = new TestPki();
        using var credential = pki.Credential("OP3R4T0RV2");

        var host = await ConnectAsync(pki.Server, pki.Ca, credential);

        // The host is NOT self-reported: it is the CN of the certificate the hub validated.
        // This is what makes `protoface@OP3R4T0RV2` trustworthy, and it is why stamping the
        // host from the socket address would not work -- over a tunnel that is always loopback.
        Assert.Equal("OP3R4T0RV2", host);
        Assert.Equal("OP3R4T0RV2", credential.Host);
    }

    [Theory]
    [InlineData("OP3R4T0RV2")]
    [InlineData("suit-01")]
    [InlineData("build.ci_02")]
    public async Task Ordinary_Host_Labels_Round_Trip_Intact(string label)
    {
        using var pki = new TestPki();
        using var credential = pki.Credential(label);

        Assert.Equal(label, await ConnectAsync(pki.Server, pki.Ca, credential));
    }

    [Theory]
    // Directory-name injection: ',' and '=' are structural in an X.500 subject.
    [InlineData("suit-01, OU=admin")]
    [InlineData("a=b")]
    // These would corrupt the hub's own AppId@Host#n identifier scheme -- and the code they
    // confuse is what decides whether an id may be launched as a LOCAL process.
    [InlineData("suit@evil")]
    [InlineData("suit#2")]
    // Whitespace, control characters, and absurd lengths.
    [InlineData("has space")]
    [InlineData("nul\0byte")]
    [InlineData("")]
    [InlineData("-leading-dash")]
    public void Dangerous_Host_Labels_Are_Refused_At_Issue_Time(string label)
    {
        using var pki = new TestPki();
        Assert.ThrowsAny<ArgumentException>(
            () => RemoteCertificates.CreateClientCertificate(pki.Ca, label, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void An_Overlong_Host_Label_Is_Refused()
    {
        using var pki = new TestPki();
        var tooLong = new string('a', RemoteCertificates.MaxHostLabelLength + 1);
        Assert.ThrowsAny<ArgumentException>(
            () => RemoteCertificates.CreateClientCertificate(pki.Ca, tooLong, TimeSpan.FromDays(1)));
    }

    // ---------------------------------------------------------------- adversarial

    [Fact]
    public async Task Client_Certificate_From_Another_CA_Is_Refused()
    {
        using var real = new TestPki("Real Hub CA");
        using var attacker = new TestPki("Attacker CA");
        using var forged = attacker.Credential("OP3R4T0RV2");

        // Same CN, different issuer. Only the signature matters.
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(real.Server, real.Ca, forged));
    }

    [Fact]
    public async Task Client_Refuses_A_Hub_Whose_Certificate_It_Does_Not_Pin()
    {
        // The direction that protects the CLIENT. Tool results carry screenshots and full UI
        // trees, so a client that could be lured to an impostor would hand over its screen.
        using var real = new TestPki("Real Hub CA");
        using var impostor = new TestPki("Impostor CA");
        using var credential = real.Credential("OP3R4T0RV2");

        await Assert.ThrowsAnyAsync<Exception>(
            () => ConnectAsync(impostor.Server, impostor.Ca, credential));
    }

    [Fact]
    public async Task A_ClientAuth_Certificate_Cannot_Be_Used_As_A_Server_Certificate()
    {
        // The confused-deputy case, and the one most easily missed. Every enrolled client
        // holds a certificate signed by the very CA that all clients pin. If that certificate
        // also passed as a server certificate, any enrolled client could stand up an impostor
        // hub and harvest another client's screenshots. Separate EKUs are what prevent it --
        // and a custom validation callback REPLACES SChannel's own EKU enforcement, so the
        // check has to be explicit. This was verified exploitable before it was added.
        using var pki = new TestPki();
        var clientAuthCert = pki.Client("sneaky-client");
        using var victim = pki.Credential("victim");

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(clientAuthCert, pki.Ca, victim));
    }

    [Fact]
    public async Task A_ServerAuth_Certificate_Cannot_Be_Used_As_A_Client_Certificate()
    {
        // The mirror image: the hub's own server certificate must not authenticate as a client.
        using var pki = new TestPki();
        var serverLeaf = pki.ServerLeaf("wrong-way-round");
        using var credential = RemoteCredential.Parse(RemoteCredential.Serialize(serverLeaf, pki.Ca));

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(pki.Server, pki.Ca, credential));
    }

    [Fact]
    public void The_CA_Certificate_Itself_Is_Not_A_Valid_Peer_Identity()
    {
        // The pinned root is in the trust store, so it chains to itself perfectly. Only the
        // explicit BasicConstraints check rejects it.
        using var pki = new TestPki();

        var accepted = RemoteTls.Validate(
            pki.Ca, System.Net.Security.SslPolicyErrors.None, pki.Ca, RemoteCertificates.ClientAuthOid);

        Assert.Null(accepted);
    }

    [Fact]
    public void An_Expired_Client_Certificate_Is_Rejected_By_The_Servers_Own_Gate()
    {
        // Asserted against the validation function directly rather than through a handshake:
        // a hostile client will happily present an expired certificate, so what matters is
        // that the SERVER refuses it, not that a cooperative client declines to send it.
        //
        // The CA has to be backdated too: a leaf's validity is clamped into its issuer's, so
        // a normally-dated CA simply cannot mint something that already expired.
        using var backdatedCa = RemoteCertificates.CreateCertificateAuthority(
            "Backdated CA", now: DateTimeOffset.UtcNow.AddYears(-2));
        using var expired = RemoteCertificates.CreateClientCertificate(
            backdatedCa, "stale", TimeSpan.FromDays(1), now: DateTimeOffset.UtcNow.AddDays(-30));

        Assert.True(expired.NotAfter < DateTime.Now, "the fixture must actually be expired");
        Assert.Null(RemoteTls.Validate(
            expired, System.Net.Security.SslPolicyErrors.None, backdatedCa, RemoteCertificates.ClientAuthOid));
    }

    [Fact]
    public void A_NotYetValid_Client_Certificate_Is_Rejected()
    {
        using var pki = new TestPki();
        using var future = RemoteCertificates.CreateClientCertificate(
            pki.Ca, "tomorrow", TimeSpan.FromDays(30), now: DateTimeOffset.UtcNow.AddDays(10));

        Assert.Null(RemoteTls.Validate(
            future, System.Net.Security.SslPolicyErrors.None, pki.Ca, RemoteCertificates.ClientAuthOid));
    }

    [Fact]
    public void A_Valid_Client_Certificate_Passes_Its_Own_Gate()
    {
        // The positive control: without this, every rejection test above could be passing
        // because the gate rejects everything.
        using var pki = new TestPki();
        var client = pki.Client("OP3R4T0RV2");

        using var accepted = RemoteTls.Validate(
            client, System.Net.Security.SslPolicyErrors.None, pki.Ca, RemoteCertificates.ClientAuthOid);

        Assert.NotNull(accepted);
        Assert.Equal("OP3R4T0RV2", RemoteCertificates.CommonNameOf(accepted!));
    }

    [Fact]
    public async Task An_Anonymous_Client_Cannot_Connect()
    {
        // ClientCertificateRequired = true, proven rather than assumed.
        using var pki = new TestPki();
        var listener = StreamTransport.CreateListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(Budget);

        var serverTask = Task.Run(async () =>
        {
            var (channel, peer, _) = await StreamTransport.AcceptAsync(listener, pki.Server, pki.Ca, cts.Token);
            await using (channel) using (peer) { return RemoteCertificates.CommonNameOf(peer); }
        }, cts.Token);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await using (ssl)
            {
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
                    {
                        TargetHost = "test-hub",
                        EnabledSslProtocols = RemoteTls.Protocols,
                    }, cts.Token);
                    // TLS 1.3 defers the client-certificate verdict, so force a round-trip.
                    await ssl.WriteAsync(new byte[] { 1 }, cts.Token);
                    await ssl.ReadAsync(new byte[16], cts.Token);
                });
            }
        }
        finally
        {
            listener.Stop();
            try { await serverTask; } catch { /* expected */ }
        }
    }

    [Fact]
    public void Validate_Rejects_A_Null_Certificate_And_An_Unavailable_One()
    {
        using var pki = new TestPki();
        Assert.Null(RemoteTls.Validate(null, System.Net.Security.SslPolicyErrors.None, pki.Ca, RemoteCertificates.ClientAuthOid));
        Assert.Null(RemoteTls.Validate(
            pki.Client("x"), System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable,
            pki.Ca, RemoteCertificates.ClientAuthOid));
    }
}
