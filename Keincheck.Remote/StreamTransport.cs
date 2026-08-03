using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Keincheck.Protocol;

namespace Keincheck.Remote;

/// <summary>
/// The TCP + TLS sibling of <c>PipeTransport</c>. Connect/accept helpers that hand back a
/// <see cref="PipeChannel"/> speaking exactly the same framed protocol as the local pipe.
/// </summary>
/// <remarks>
/// The framing, the message set and the version handshake are unchanged from the pipe — the
/// codec was always written against an arbitrary <see cref="Stream"/>. Only the way the stream
/// is obtained, and who is allowed to obtain one, is new.
/// </remarks>
public static class StreamTransport
{
    /// <summary>Idle time before the first TCP keepalive probe.</summary>
    public static readonly TimeSpan KeepAliveTime = TimeSpan.FromSeconds(15);

    /// <summary>Gap between TCP keepalive probes.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);

    /// <summary>Unanswered keepalive probes before the OS declares the connection dead.</summary>
    public const int KeepAliveRetryCount = 4;

    // ---------------------------------------------------------------- client side

    /// <summary>
    /// Dials <paramref name="endpoint"/>, completes mutual TLS using
    /// <paramref name="credential"/>, and returns a channel ready for the Hello exchange.
    /// </summary>
    /// <remarks>
    /// Retries with the same backoff shape as the pipe connector (25 ms doubling to a 500 ms
    /// cap, until the deadline), because the common case here is identical: the hub is not up
    /// *yet*. Connection refused is retried; a TLS rejection is not — if the hub actively
    /// refused this credential, hammering it cannot help and would only trip its rate limiter.
    /// </remarks>
    public static async Task<PipeChannel> ConnectAsync(
        RemoteEndpoint endpoint,
        RemoteCredential credential,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var delayMs = 25;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tcp = new TcpClient();
            Stream? network = null;
            try
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"No Keincheck hub reachable at {endpoint} within the timeout.");

                await tcp.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
                Configure(tcp.Client);
                network = tcp.GetStream();

                // TargetHost is required by the API but not used for identity: hostname
                // validation is meaningless when the peer is a tunnel's loopback end, so
                // RemoteTls authenticates against the pinned CA and the certificate CN instead.
                var ssl = await RemoteTls.AuthenticateAsClientAsync(
                    network, credential.ClientCertificate, credential.CertificateAuthority,
                    endpoint.Host, cancellationToken).ConfigureAwait(false);

                // ssl now owns the NetworkStream (leaveInnerStreamOpen: false) and the channel
                // owns ssl, so disposing the channel unwinds the whole chain down to the socket.
                network = null;
                return new PipeChannel(ssl, ownsStream: true, ChannelLimits.Default);
            }
            catch (SocketException)
            {
                // Nothing listening yet (or the tunnel is not up): back off and retry.
                network?.Dispose();
                tcp.Dispose();
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 500);
            }
            catch
            {
                network?.Dispose();
                tcp.Dispose();
                throw;
            }
        }
    }

    // ---------------------------------------------------------------- server side

    /// <summary>
    /// Creates a listener bound to <paramref name="bindAddress"/>. Binding a non-loopback
    /// address is permitted — mutual TLS, not the network boundary, is what protects the hub —
    /// but it is always a deliberate configuration choice, never a default.
    /// </summary>
    public static TcpListener CreateListener(IPAddress bindAddress, int port)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        var listener = new TcpListener(bindAddress, port);
        // Do NOT set ExclusiveAddressUse=false: allowing another process to share the port
        // would let it intercept sessions.
        listener.ExclusiveAddressUse = true;
        return listener;
    }

    /// <summary>
    /// Accepts one connection and completes mutual TLS, returning the channel and the
    /// validated peer certificate the caller derives the host label from.
    /// </summary>
    /// <remarks>
    /// The channel starts at <see cref="ChannelLimits.Handshake"/>. TLS proves the peer holds a
    /// hub-issued certificate, but the session is not yet accepted — it may still be revoked
    /// or speak an unsupported version — so it does not get full-size framing until the
    /// listener says so.
    /// </remarks>
    public static async Task<(PipeChannel Channel, X509Certificate2 PeerCertificate, IPEndPoint? Peer)> AcceptAsync(
        TcpListener listener,
        X509Certificate2 serverCertificate,
        X509Certificate2 certificateAuthority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listener);

        var tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        Stream? network = null;
        try
        {
            Configure(tcp.Client);
            var peer = tcp.Client.RemoteEndPoint as IPEndPoint;
            network = tcp.GetStream();

            var (ssl, peerCertificate) = await RemoteTls.AuthenticateAsServerAsync(
                network, serverCertificate, certificateAuthority, cancellationToken).ConfigureAwait(false);

            network = null;
            return (new PipeChannel(ssl, ownsStream: true, ChannelLimits.Handshake), peerCertificate, peer);
        }
        catch
        {
            network?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- socket options

    /// <summary>
    /// Applies the socket options every Keincheck TCP session wants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Socket.NoDelay"/> because the protocol is request/response over small
    /// frames, and Nagle would add latency to every tool call for no benefit.
    /// </para>
    /// <para>
    /// Keepalive because a named pipe reports a dead peer immediately as EOF and TCP does not.
    /// Without it, a suit that drives out of wifi range leaves the hub holding a socket that
    /// looks perfectly healthy until the application-level heartbeat watchdog notices. The
    /// keepalive gives the OS a chance to tear it down first.
    /// </para>
    /// </remarks>
    public static void Configure(Socket socket)
    {
        socket.NoDelay = true;
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime,
                (int)KeepAliveTime.TotalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval,
                (int)KeepAliveInterval.TotalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount,
                KeepAliveRetryCount);
        }
        catch (SocketException)
        {
            // Keepalive tuning is unsupported on some platforms. It is a nicety on top of the
            // application-level heartbeat watchdog, which remains the real backstop -- so a
            // platform that refuses it must not prevent the connection.
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
