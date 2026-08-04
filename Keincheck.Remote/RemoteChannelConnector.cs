using System.Diagnostics;
using Keincheck.Protocol;

namespace Keincheck.Remote;

/// <summary>
/// Connects a Keincheck client to a hub on another machine over mutually-authenticated TLS.
/// </summary>
/// <remarks>
/// <para>
/// Assign one to <c>McpClientOptions.Connector</c> to make an app remotely debuggable:
/// </para>
/// <code>
/// builder.UseMcpClient(o =>
/// {
///     o.AppId = "myapp";
///     o.Connector = RemoteChannelConnector.FromEnvironment()          // KEINCHECK_REMOTE[_FILE]
///                   ?? RemoteChannelConnector.FromBundle(BakedCredential);
/// });
/// </code>
/// <para>
/// The credential is parsed <b>once</b> and its certificates are reused for the lifetime of
/// the connector. That is not merely an optimisation: TLS on Windows refuses ephemerally-keyed
/// certificates, so each parse occupies a key container until disposal, and re-parsing per
/// reconnect would accumulate them on precisely the flaky link that reconnects most.
/// </para>
/// </remarks>
public sealed class RemoteChannelConnector : IChannelConnector, IDisposable
{
    private readonly RemoteCredential _credential;
    private readonly RemoteEndpoint _endpoint;
    private readonly TimeSpan _connectTimeout;
    private readonly bool _ownsCredential;
    private int _disposed;

    /// <param name="credential">The hub-issued credential. Kept for the connector's lifetime.</param>
    /// <param name="endpoint">Where to dial; the credential's own default when null.</param>
    /// <param name="connectTimeout">How long to keep retrying a connect before failing an attempt.</param>
    /// <param name="ownsCredential">When true (default) disposing the connector disposes the credential.</param>
    /// <exception cref="ArgumentException">No endpoint was supplied and the credential carries none.</exception>
    public RemoteChannelConnector(
        RemoteCredential credential,
        RemoteEndpoint? endpoint = null,
        TimeSpan? connectTimeout = null,
        bool ownsCredential = true)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _endpoint = endpoint ?? credential.DefaultEndpoint
            ?? throw new ArgumentException(
                "No endpoint was given and the credential does not carry one. Pass an endpoint, " +
                "or re-issue the credential with the hub's address baked in.", nameof(endpoint));
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30);
        _ownsCredential = ownsCredential;
    }

    /// <summary>Builds a connector from a credential bundle string.</summary>
    public static RemoteChannelConnector FromBundle(
        string bundle, RemoteEndpoint? endpoint = null, TimeSpan? connectTimeout = null)
        => new(RemoteCredential.Parse(bundle), endpoint, connectTimeout);

    /// <summary>Builds a connector from a credential bundle file.</summary>
    public static RemoteChannelConnector FromFile(
        string path, RemoteEndpoint? endpoint = null, TimeSpan? connectTimeout = null)
        => new(RemoteCredential.LoadFile(path), endpoint, connectTimeout);

    /// <summary>
    /// Builds a connector from <c>KEINCHECK_REMOTE_FILE</c> / <c>KEINCHECK_REMOTE</c>, or
    /// returns null when neither is set — so an app can offer remote access without requiring
    /// it, and fall back to the local pipe by leaving <c>Connector</c> null.
    /// </summary>
    /// <exception cref="FormatException">A variable was set but its contents are unusable.</exception>
    public static RemoteChannelConnector? FromEnvironment(
        RemoteEndpoint? endpoint = null, TimeSpan? connectTimeout = null)
    {
        var credential = RemoteCredential.FromEnvironment();
        return credential is null ? null : new RemoteChannelConnector(credential, endpoint, connectTimeout);
    }

    /// <summary>The host label this connector authenticates as.</summary>
    public string Host => _credential.Host;

    /// <summary>Where this connector dials.</summary>
    public RemoteEndpoint Endpoint => _endpoint;

    /// <inheritdoc/>
    public async Task<ChannelSession> ConnectAsync(
        ChannelConnectContext context, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(context);

        // Fail with the real reason rather than an opaque TLS error, which is otherwise a
        // genuinely baffling thing to debug on a headless machine.
        if (DateTimeOffset.UtcNow >= _credential.NotAfter)
        {
            // Permanent: an expired credential does not become valid by waiting, so the client
            // loop must stop rather than handshake against the hub every few seconds forever.
            throw new ChannelConnectRefusedException(
                $"The remote credential for '{_credential.Host}' expired on " +
                $"{_credential.NotAfter:u}. Re-issue it from the hub.",
                isPermanent: true);
        }

        var channel = await StreamTransport
            .ConnectAsync(_endpoint, _credential, _connectTimeout, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var result = await RemoteHandshake
                .ClientAsync(channel, context.ClientVersion, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(result.Host, _credential.Host, StringComparison.OrdinalIgnoreCase))
            {
                // The hub derives the host from the certificate it just validated, so a
                // mismatch means we are not talking to the hub we think we are.
                throw new ProtocolException(
                    $"The hub assigned host '{result.Host}' but this credential is for " +
                    $"'{_credential.Host}'.");
            }

            return new ChannelSession
            {
                Channel = channel,
                // Derived from the interval the hub PROMISED, not from our own send interval.
                // A pipe needs no such deadline at all, because a dead peer closes it; a
                // black-holed TCP session is silent forever and would otherwise hang here.
                //
                // If the hub promised nothing, we wait indefinitely rather than inventing a
                // deadline it was never going to satisfy — a self-inflicted disconnect loop is
                // worse than a hung session, because it looks like a flaky network.
                ReadTimeout = result.ServerHeartbeat is { } beat
                    ? beat * RemoteHandshake.MissedHeartbeatsBeforeDrop
                    : null,
                Description = Describe(),
            };
        }
        catch (RemoteHandshake.RejectedException ex)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            Debug.WriteLine($"[Keincheck.Remote] {ex.Message}");
            throw;
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public string Describe() => $"tls {_endpoint} as '{_credential.Host}'";

    /// <inheritdoc/>
    /// <remarks>
    /// A remote session genuinely needs v2 — it opens with a Hello the older protocol has no
    /// message kind for. Unlike the pipe, a version mismatch here is reported to the peer with
    /// a reason before the connection closes.
    /// </remarks>
    public int AdvertisedProtocolVersion => ProtocolVersion.Current;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_ownsCredential)
            _credential.Dispose();
    }
}
