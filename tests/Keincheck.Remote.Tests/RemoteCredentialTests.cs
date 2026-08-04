using System.Security.Cryptography.X509Certificates;
using Keincheck.Remote;
using Xunit;

namespace Keincheck.Remote.Tests;

/// <summary>
/// Tests for the enrollment bundle — the single opaque string a hub issues and an app bakes
/// in, drops in a file, or sets as an environment variable.
/// </summary>
public sealed class RemoteCredentialTests
{
    [Fact]
    public void A_Bundle_Round_Trips_Its_Identity_And_Trust_Root()
    {
        using var pki = new TestPki();
        var endpoint = new RemoteEndpoint { Host = "hub.local", Port = 7423 };

        var bundle = RemoteCredential.Serialize(pki.Client("MACHINENAME"), pki.Ca, endpoint);
        using var credential = RemoteCredential.Parse(bundle);

        Assert.StartsWith(RemoteCredential.Prefix, bundle);
        Assert.Equal("MACHINENAME", credential.Host);
        Assert.True(credential.ClientCertificate.HasPrivateKey, "the client must be able to authenticate");
        Assert.False(credential.CertificateAuthority.HasPrivateKey,
            "the CA's PRIVATE key must never leave the hub -- a client holding it could mint its own credentials");
        Assert.Equal(endpoint, credential.DefaultEndpoint);
        Assert.Equal(pki.Ca.Thumbprint, credential.CertificateAuthority.Thumbprint);
    }

    [Theory]
    [InlineData("hub.local", "hub.local", RemoteEndpoint.DefaultPort)]
    [InlineData("hub.local:9000", "hub.local", 9000)]
    [InlineData("192.168.1.5:7423", "192.168.1.5", 7423)]
    [InlineData("[::1]:9000", "::1", 9000)]
    [InlineData("::1", "::1", RemoteEndpoint.DefaultPort)]
    public void Endpoints_Parse(string text, string host, int port)
    {
        Assert.True(RemoteEndpoint.TryParse(text, out var endpoint));
        Assert.Equal(host, endpoint!.Host);
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    // A bare ":port" has no host. It used to "succeed" as Host=":9000", Port=7423 — and that
    // value is baked into every credential issued while it is configured, so the failure only
    // surfaces as an unexplained connect failure on a different machine.
    [InlineData(":9000")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:notaport")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:-1")]
    [InlineData("[::1")]
    public void Malformed_Endpoints_Are_Refused(string text)
    {
        Assert.False(RemoteEndpoint.TryParse(text, out var endpoint));
        Assert.Null(endpoint);
    }

    [Fact]
    public void A_Bundle_Is_Safe_To_Paste_Anywhere()
    {
        // It has to survive a C# string literal, a shell variable, and a URL unescaped --
        // those are the three ways it actually reaches an app.
        using var pki = new TestPki();
        var bundle = RemoteCredential.Serialize(pki.Client("remotehost"), pki.Ca);

        Assert.DoesNotContain('+', bundle);
        Assert.DoesNotContain('/', bundle);
        Assert.DoesNotContain('=', bundle);
        Assert.DoesNotContain('"', bundle);
        Assert.DoesNotContain('\\', bundle);
        Assert.DoesNotContain('\n', bundle);
    }

    [Fact]
    public void A_Bundle_Without_An_Endpoint_Parses_And_Reports_None()
    {
        using var pki = new TestPki();
        using var credential = RemoteCredential.Parse(RemoteCredential.Serialize(pki.Client("remotehost"), pki.Ca));

        Assert.Null(credential.DefaultEndpoint);
        // ...and a connector built from it must then insist on being told where to dial,
        // rather than guessing a default that could be someone else's hub.
        Assert.Throws<ArgumentException>(() => new RemoteChannelConnector(credential, ownsCredential: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-bundle")]
    [InlineData("kcrem1_")]
    [InlineData("kcrem1_!!!!not-base64!!!!")]
    [InlineData("kcrem1_aGVsbG8")]                      // valid base64url, not valid JSON
    public void Garbage_Is_Refused_With_A_FormatException(string text)
    {
        Assert.Throws<FormatException>(() => RemoteCredential.Parse(text));
    }

    [Fact]
    public void A_Tampered_Bundle_Is_Refused()
    {
        using var pki = new TestPki();
        var bundle = RemoteCredential.Serialize(pki.Client("remotehost"), pki.Ca);

        // Flip a character in the middle of the payload.
        var chars = bundle.ToCharArray();
        var i = bundle.Length / 2;
        chars[i] = chars[i] == 'A' ? 'B' : 'A';

        Assert.ThrowsAny<Exception>(() => RemoteCredential.Parse(new string(chars)));
    }

    [Fact]
    public void A_Bundle_Missing_Its_Prefix_Says_So_Plainly()
    {
        using var pki = new TestPki();
        var bundle = RemoteCredential.Serialize(pki.Client("remotehost"), pki.Ca);

        var ex = Assert.Throws<FormatException>(() => RemoteCredential.Parse(bundle[RemoteCredential.Prefix.Length..]));
        Assert.Contains(RemoteCredential.Prefix, ex.Message);
    }

    [Fact]
    public void Serializing_Without_A_Private_Key_Is_Refused()
    {
        // A bundle with no private key would produce a credential that cannot authenticate,
        // failing later as an opaque TLS error instead of here as an obvious mistake.
        using var pki = new TestPki();
        using var publicOnly = RemoteCertificates.LoadPublic(pki.Client("remotehost").Export(X509ContentType.Cert));

        Assert.Throws<ArgumentException>(() => RemoteCredential.Serialize(publicOnly, pki.Ca));
    }

    [Fact]
    public void Expiry_Is_Reported_Before_It_Bites()
    {
        using var pki = new TestPki();
        using var credential = pki.Credential("remotehost", lifetime: TimeSpan.FromDays(10));

        Assert.False(credential.IsExpiringWithin(TimeSpan.FromDays(5)));
        Assert.True(credential.IsExpiringWithin(TimeSpan.FromDays(30)));
        Assert.True(credential.NotAfter > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task An_Expired_Credential_Fails_With_A_Readable_Reason_Not_A_TLS_Error()
    {
        // Debugging "the handshake failed" on a headless machine is miserable. The connector
        // checks expiry itself so the message names the actual problem.
        using var backdatedCa = RemoteCertificates.CreateCertificateAuthority(
            "Backdated", now: DateTimeOffset.UtcNow.AddYears(-2));
        using var expired = RemoteCertificates.CreateClientCertificate(
            backdatedCa, "stale", TimeSpan.FromDays(1), now: DateTimeOffset.UtcNow.AddDays(-30));

        var bundle = RemoteCredential.Serialize(expired, backdatedCa,
            new RemoteEndpoint { Host = "127.0.0.1", Port = 1 });
        using var connector = RemoteChannelConnector.FromBundle(bundle);

        var ex = await Assert.ThrowsAsync<Keincheck.Protocol.ChannelConnectRefusedException>(() =>
            connector.ConnectAsync(new Keincheck.Protocol.ChannelConnectContext { AppId = "x" }, default));

        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale", ex.Message);

        // And marked permanent, so the client's reconnect loop STOPS instead of completing a
        // full TLS handshake every few seconds forever against a credential that cannot work.
        Assert.True(ex.IsPermanent);
    }

    [Fact]
    public void Environment_Resolution_Prefers_A_File_Over_An_Inline_Bundle()
    {
        // A path is the safer way to supply a credential -- it keeps the secret out of the
        // process's environment block -- so it must not be silently overridden by an inline one.
        using var pki = new TestPki();
        var fileBundle = RemoteCredential.Serialize(pki.Client("from-file"), pki.Ca);
        var inlineBundle = RemoteCredential.Serialize(pki.Client("from-inline"), pki.Ca);

        var path = Path.Combine(Path.GetTempPath(), $"kcrem-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, fileBundle);
        try
        {
            Environment.SetEnvironmentVariable(RemoteCredential.FileEnvironmentVariable, path);
            Environment.SetEnvironmentVariable(RemoteCredential.BundleEnvironmentVariable, inlineBundle);

            using var resolved = RemoteCredential.FromEnvironment();
            Assert.Equal("from-file", resolved!.Host);

            Environment.SetEnvironmentVariable(RemoteCredential.FileEnvironmentVariable, null);
            using var inlineOnly = RemoteCredential.FromEnvironment();
            Assert.Equal("from-inline", inlineOnly!.Host);

            Environment.SetEnvironmentVariable(RemoteCredential.BundleEnvironmentVariable, null);
            Assert.Null(RemoteCredential.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(RemoteCredential.FileEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(RemoteCredential.BundleEnvironmentVariable, null);
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Missing_Credential_File_Is_Reported_Rather_Than_Ignored()
    {
        // Silently falling through to "no credential" would let an app that was configured for
        // remote quietly attach to the LOCAL hub instead -- driving the wrong machine while
        // looking completely normal.
        var missing = Path.Combine(Path.GetTempPath(), $"kcrem-missing-{Guid.NewGuid():N}.txt");
        try
        {
            Environment.SetEnvironmentVariable(RemoteCredential.FileEnvironmentVariable, missing);
            var ex = Assert.Throws<FormatException>(() => RemoteCredential.FromEnvironment());
            Assert.Contains(missing, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RemoteCredential.FileEnvironmentVariable, null);
        }
    }

    [Fact]
    public void Disposing_A_Credential_Releases_Both_Certificates()
    {
        // Not hygiene for its own sake: TLS on Windows refuses ephemeral keys, so each loaded
        // certificate holds a key container until disposal.
        using var pki = new TestPki();
        var credential = RemoteCredential.Parse(RemoteCredential.Serialize(pki.Client("remotehost"), pki.Ca));

        credential.Dispose();
        credential.Dispose(); // idempotent

        Assert.ThrowsAny<Exception>(() => _ = credential.ClientCertificate.RawData);
    }
}
