using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Keincheck.Remote;

/// <summary>
/// Wraps a byte stream in mutually-authenticated TLS, trusting only the hub's own CA.
/// </summary>
/// <remarks>
/// <para>
/// Both directions authenticate: the hub refuses a client it did not issue a certificate to,
/// and the client refuses a hub that does not hold the CA it was enrolled against. The
/// second half matters as much as the first — tool <i>results</i> carry PNG screenshots and
/// full UI trees, so a client that could be talked into connecting to an impostor would hand
/// its screen to it.
/// </para>
/// <para>
/// Because the trust root is the hub's own CA rather than a public one, the OS trust store is
/// never consulted. There is also no hostname validation: over an SSH tunnel the peer address
/// is always <c>127.0.0.1</c>, so a name check would be theatre. Identity comes from the CA
/// plus the certificate's common name.
/// </para>
/// </remarks>
public static class RemoteTls
{
    /// <summary>TLS versions permitted. Older suites are not negotiable.</summary>
    public const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>
    /// Authenticates as the hub: presents <paramref name="serverCertificate"/> and demands a
    /// client certificate chaining to <paramref name="certificateAuthority"/>.
    /// </summary>
    /// <returns>The TLS stream, and the validated peer certificate the caller derives identity from.</returns>
    public static async Task<(SslStream Stream, X509Certificate2 PeerCertificate)> AuthenticateAsServerAsync(
        Stream inner,
        X509Certificate2 serverCertificate,
        X509Certificate2 certificateAuthority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        ArgumentNullException.ThrowIfNull(certificateAuthority);

        X509Certificate2? peer = null;

        // leaveInnerStreamOpen: false so disposing this stream closes the NetworkStream and
        // the socket beneath it. That is what makes PipeChannel(ownsStream: true) release
        // the whole chain on a dropped session instead of leaking a socket per reconnect.
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, (_, cert, _, errors) =>
        {
            var validated = Validate(cert, errors, certificateAuthority, RemoteCertificates.ClientAuthOid);
            peer = validated;
            return validated is not null;
        });

        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            peer?.Dispose();
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (peer is null)
        {
            // Belt and braces: the callback must have run and produced a peer for the
            // handshake to have succeeded. Refuse rather than proceed unidentified.
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException("TLS completed without a validated client certificate.");
        }

        return (ssl, peer);
    }

    /// <summary>
    /// Authenticates as a client: presents <paramref name="clientCertificate"/> and requires
    /// the hub's certificate to chain to <paramref name="certificateAuthority"/>.
    /// </summary>
    public static async Task<SslStream> AuthenticateAsClientAsync(
        Stream inner,
        X509Certificate2 clientCertificate,
        X509Certificate2 certificateAuthority,
        string targetHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        ArgumentNullException.ThrowIfNull(certificateAuthority);

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, (_, cert, _, errors) =>
        {
            var validated = Validate(cert, errors, certificateAuthority, RemoteCertificates.ServerAuthOid);
            validated?.Dispose();
            return validated is not null;
        });

        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ClientCertificates = [clientCertificate],
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return ssl;
    }

    /// <summary>
    /// The single peer-validation gate. Returns the validated certificate, or <c>null</c> to
    /// reject. Callers own the returned instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three checks, and every one of them earns its place:
    /// </para>
    /// <list type="number">
    ///   <item><b>Not a CA.</b> The pinned root is in the trust store, so presenting the CA
    ///   certificate itself as a peer identity chains perfectly cleanly. Not reachable over
    ///   TLS without the CA private key, but one line to close and a nasty surprise if the
    ///   trust store ever grows a second entry.</item>
    ///   <item><b>Chains to the pinned CA</b>, with the OS trust store excluded entirely
    ///   (<see cref="X509ChainTrustMode.CustomRootTrust"/>). Expiry and not-yet-valid are
    ///   enforced by the chain build.</item>
    ///   <item><b>Correct extended key usage</b> — via
    ///   <see cref="X509ChainPolicy.ApplicationPolicy"/>. <b>This one is not optional and is
    ///   easy to omit.</b> Supplying a custom
    ///   <see cref="RemoteCertificateValidationCallback"/> <i>replaces</i> SChannel's own EKU
    ///   enforcement rather than supplementing it. Without this line a certificate issued
    ///   only for client authentication is accepted as a <i>server</i> certificate — and
    ///   since every enrolled client holds one signed by the CA that all clients pin, any
    ///   enrolled client could stand up an impostor hub and harvest another client's
    ///   screenshots. Verified exploitable before this check existed.</item>
    /// </list>
    /// </remarks>
    internal static X509Certificate2? Validate(
        X509Certificate? presented,
        SslPolicyErrors errors,
        X509Certificate2 certificateAuthority,
        string requiredEkuOid)
    {
        if (presented is null)
            return null;
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return null;

        X509Certificate2? leaf = null;
        try
        {
            leaf = RemoteCertificates.LoadPublic(presented.Export(X509ContentType.Cert));

            foreach (var extension in leaf.Extensions)
            {
                if (extension is X509BasicConstraintsExtension { CertificateAuthority: true })
                    return null;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificateAuthority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid(requiredEkuOid));

            if (!chain.Build(leaf))
                return null;

            var validated = leaf;
            leaf = null; // ownership transfers to the caller
            return validated;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // X509Chain.Build throws rather than returning false in some states (notably a
            // disposed certificate in the trust store). Fail closed instead of letting it
            // escape the validation callback as an opaque handshake error.
            return null;
        }
        finally
        {
            leaf?.Dispose();
        }
    }
}
