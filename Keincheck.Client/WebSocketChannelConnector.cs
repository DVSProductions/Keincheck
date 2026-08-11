using System.Net.WebSockets;
using Keincheck.Protocol;

namespace Keincheck.Client;

// Implements Keincheck.Protocol.IChannelConnector, alongside PipeChannelConnector. The pipe is
// the default everywhere a pipe exists; this is for the one place none does.

/// <summary>
/// Reaches the hub over a WebSocket instead of a named pipe.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists for the browser.</b> WebAssembly has no named pipes, no sockets, no
/// <c>SslStream</c> and no <c>X509Certificate2</c> — a browser-hosted Avalonia app can reach
/// nothing through <see cref="PipeChannelConnector"/> or <c>Keincheck.Remote</c>. What it does
/// have is the browser's own WebSocket API, which <see cref="ClientWebSocket"/> is implemented
/// on top of there. Everything above the socket is unchanged: the session is a
/// <see cref="PipeChannel"/> over a <see cref="WebSocketStream"/>, so framing, chunking and the
/// register handshake are the same code every other transport runs.
/// </para>
/// <para>
/// <b>The security model is not Keincheck.Remote's, and must not be mistaken for it.</b> There
/// is no mutual TLS here and there cannot be — a browser cannot present a client certificate or
/// pin a private CA. Authentication is a token the hub issues, and confidentiality comes from
/// the transport underneath: loopback (where the bytes never leave the machine) or <c>wss</c>
/// with a certificate the browser already trusts. Pointing this at a plain <c>ws://</c> host
/// that is not loopback sends everything in the clear, so <see cref="ConnectAsync"/> refuses it.
/// </para>
/// </remarks>
public sealed class WebSocketChannelConnector : IChannelConnector
{
    private readonly Uri _endpoint;
    private readonly string? _token;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan? _readTimeout;

    /// <param name="endpoint">
    /// The hub's WebSocket endpoint, e.g. <c>ws://127.0.0.1:3100/ws</c> or <c>wss://host/ws</c>.
    /// </param>
    /// <param name="token">
    /// The token the hub issued for this app. Null only when the hub has the gate disabled.
    /// </param>
    /// <param name="connectTimeout">How long to keep trying before giving up on one attempt.</param>
    /// <param name="readTimeout">
    /// Tear the session down if no frame arrives within this window. Defaults to null on
    /// loopback and 30s otherwise: a loopback peer that dies surfaces immediately, exactly like
    /// a pipe, but a networked one can black-hole a connection so that a dead link is
    /// indistinguishable from a quiet one.
    /// </param>
    public WebSocketChannelConnector(
        Uri endpoint,
        string? token = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Scheme is not ("ws" or "wss"))
            throw new ArgumentException($"Expected a ws:// or wss:// endpoint, got '{endpoint.Scheme}'.", nameof(endpoint));

        // Refuse plaintext to anywhere but loopback. A hub reachable over ws:// on a real
        // network would carry screenshots and UI trees of the app in the clear, and the token
        // with them -- and it would do it silently, which is the worst way to be insecure.
        if (endpoint.Scheme == "ws" && !IsLoopback(endpoint))
        {
            throw new ArgumentException(
                $"Refusing plaintext ws:// to non-loopback host '{endpoint.Host}'. " +
                "Use wss:// with a certificate the browser trusts.",
                nameof(endpoint));
        }

        _endpoint = endpoint;
        _token = token;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        _readTimeout = readTimeout ?? (IsLoopback(endpoint) ? null : TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Builds a connector from a <see cref="WebSocketCredential"/>: the environment's, if one is
    /// set, otherwise the one the build embedded in <paramref name="assembly"/>. Returns null
    /// when there is neither, so an app can offer WebSocket attach without requiring it.
    /// </summary>
    /// <remarks>
    /// The environment wins over the embedded credential on purpose, mirroring
    /// <c>Keincheck.Remote</c>: it is what lets one build be pointed at a different hub without
    /// recompiling, which is the difference between testing against a colleague's machine and
    /// not being able to.
    /// </remarks>
    public static WebSocketChannelConnector? FromCredential(
        System.Reflection.Assembly? assembly = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null)
    {
        var credential = WebSocketCredential.FromEnvironment()
            ?? WebSocketCredential.FromAssembly(
                assembly ?? System.Reflection.Assembly.GetEntryAssembly()
                ?? System.Reflection.Assembly.GetCallingAssembly());

        return credential is null
            ? null
            : new WebSocketChannelConnector(
                new Uri(credential.Endpoint), credential.Token, connectTimeout, readTimeout);
    }

    /// <inheritdoc/>
    public async Task<ChannelSession> ConnectAsync(
        ChannelConnectContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var socket = new ClientWebSocket();
        try
        {
            // Lets a refusal be classified as permanent instead of retried forever. The browser
            // WebSocket API does not surface the HTTP response at all, so this is best-effort
            // and its absence simply means every failure looks retryable there.
            try { socket.Options.CollectHttpResponseDetails = true; }
            catch (PlatformNotSupportedException) { /* browser */ }

            // Sized off the client's own heartbeat so the transport does not invent a second,
            // conflicting liveness rhythm. Ignored on browser, where the runtime owns pings.
            try { socket.Options.KeepAliveInterval = context.HeartbeatInterval; }
            catch (PlatformNotSupportedException) { /* browser */ }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(_connectTimeout);

            try
            {
                await socket.ConnectAsync(BuildUri(context), attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"No Keincheck hub answered at {Describe()} within {_connectTimeout.TotalSeconds:0.#}s.");
            }
            catch (WebSocketException ex)
            {
                throw Classify(ex, socket);
            }

            var channel = new PipeChannel(new WebSocketStream(socket));
            socket = null; // ownership moved to the channel
            return new ChannelSession
            {
                Channel = channel,
                ReadTimeout = _readTimeout,
                Description = Describe(),
            };
        }
        finally
        {
            socket?.Dispose();
        }
    }

    /// <summary>
    /// Turns a failed handshake into a refusal the reconnect loop can act on. A rejected or
    /// missing token cannot succeed on retry -- retrying it is a hot loop that logs an
    /// authentication failure at the hub every few seconds forever.
    /// </summary>
    private ChannelConnectRefusedException Classify(WebSocketException ex, ClientWebSocket socket)
    {
        var status = (int?)socket.HttpStatusCode;
        var permanent = status is 400 or 401 or 403 or 404;

        var reason = status switch
        {
            401 or 403 => "the hub rejected the token",
            400 => "the hub rejected the request (origin not allowed?)",
            404 => "the hub is not serving a WebSocket endpoint at this path",
            _ => "the WebSocket handshake failed",
        };

        return new ChannelConnectRefusedException($"{Describe()}: {reason}.", permanent, ex);
    }

    /// <summary>
    /// The endpoint with the token and the app id attached. The app id is a hint for the hub's
    /// consent prompt only -- the authoritative one still arrives in <c>RegisterMessage</c>.
    /// </summary>
    /// <remarks>
    /// The token rides in the query string because a browser cannot set request headers on a
    /// WebSocket -- the JavaScript WebSocket API exposes no way to do it, so the usual
    /// Authorization header is unavailable on the one platform this connector serves. Query
    /// strings can land in server logs, which is why the hub's tokens are per-app, revocable,
    /// and worthless off the machine that issued them.
    /// </remarks>
    private Uri BuildUri(ChannelConnectContext context)
    {
        var query = _endpoint.Query.TrimStart('?');

        void Append(string key, string value)
        {
            if (query.Length > 0)
                query += "&";
            query += $"{key}={Uri.EscapeDataString(value)}";
        }

        if (!string.IsNullOrEmpty(_token))
            Append(WebSocketEndpoint.TokenQueryParameter, _token);
        Append(WebSocketEndpoint.AppIdQueryParameter, context.AppId);

        return new UriBuilder(_endpoint) { Query = query }.Uri;
    }

    /// <inheritdoc/>
    /// <remarks>Never includes the token: this string goes to logs and error messages.</remarks>
    public string Describe()
        => $"websocket '{_endpoint.Scheme}://{_endpoint.Authority}{_endpoint.AbsolutePath}'";

    // AdvertisedProtocolVersion is deliberately left at the interface default (Minimum). This
    // transport performs no v2-specific handshake -- it is the pipe's register flow on a
    // different set of bytes -- so advertising Current would only make an older hub refuse it.

    private static bool IsLoopback(Uri uri)
        => uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}
