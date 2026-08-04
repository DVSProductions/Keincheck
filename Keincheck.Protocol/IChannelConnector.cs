namespace Keincheck.Protocol;

/// <summary>
/// How a client obtains a connected <see cref="PipeChannel"/> to the hub.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps remote access an opt-in <i>package</i> rather than a flag.
/// <c>Keincheck.Client</c> ships exactly one implementation — the local named pipe — and
/// knows nothing about sockets, TLS or certificates. An app that wants to be reachable from
/// another machine installs <c>Keincheck.Remote</c>, which supplies its own connector. An app
/// that does not has no networking code linked at all, so remote debuggability cannot be
/// switched on by accident or left as latent attack surface.
/// </para>
/// <para>
/// A connector returns a channel that is ready for <see cref="MessageKind.Register"/>: any
/// transport-level handshake (TLS, capability negotiation) is the connector's business and
/// is finished before it hands the channel back. That is deliberate — it means the client's
/// session loop is identical on every transport.
/// </para>
/// <para>
/// It lives in this assembly rather than <c>Keincheck.Client</c> so that
/// <c>Keincheck.Remote</c> — which the <i>hub</i> also consumes for its listener — needs only
/// this zero-dependency assembly, and never drags the client's tool host and the MCP SDK
/// into the hub process.
/// </para>
/// </remarks>
public interface IChannelConnector
{
    /// <summary>
    /// Connects to the hub, completing any transport handshake, and returns the established
    /// session. Throws on failure; the caller retries with backoff.
    /// </summary>
    /// <remarks>
    /// Implementations must <b>not</b> silently fall back to a different transport. An app
    /// configured to reach a remote hub that instead attaches to the local one would let the
    /// operator drive the wrong machine while believing otherwise — fail loudly instead.
    /// </remarks>
    Task<ChannelSession> ConnectAsync(ChannelConnectContext context, CancellationToken cancellationToken);

    /// <summary>A short, non-secret description of the target, for logs and error messages.</summary>
    string Describe();

    /// <summary>
    /// The protocol version the client should advertise in its <see cref="RegisterMessage"/>
    /// on this transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A client must advertise the lowest version that covers what it actually <i>uses</i>, not
    /// the newest it knows about — otherwise a routine package update becomes an outage.
    /// </para>
    /// <para>
    /// On the local pipe a v2 client uses nothing v2-specific: Register, ToolList, InvokeTool
    /// and ToolResult are byte-identical to v1, <see cref="ToolDescriptor.ReadOnly"/> is an
    /// additive field an older hub ignores, and compression is never enabled without a
    /// handshake the pipe does not perform. Advertising v2 there would have made an older hub
    /// compute <c>IsCompatible(2) == false</c> and drop the connection without a word — so
    /// updating the client package before the hub auto-updated would look like "my app just
    /// stopped connecting". It therefore advertises <see cref="ProtocolVersion.Minimum"/>.
    /// </para>
    /// <para>
    /// A remote transport advertises <see cref="ProtocolVersion.Current"/>, because its
    /// handshake genuinely requires v2 — and there, a mismatch is reported to the peer with a
    /// reason instead of dropping the connection silently.
    /// </para>
    /// </remarks>
    int AdvertisedProtocolVersion => ProtocolVersion.Minimum;
}

/// <summary>
/// A connection attempt was refused by the peer, with a verdict on whether retrying could
/// ever help.
/// </summary>
/// <remarks>
/// The distinction is the point. A hub that is not up yet, or is briefly overloaded, is worth
/// reconnecting to — that is the normal case for a device whose link comes and goes. A revoked
/// credential, an expired one, or an unsupported protocol version is not: retrying is a hot
/// loop that completes a full TLS handshake every few seconds forever, and is recorded as an
/// authentication failure at the other end every time.
/// </remarks>
public class ChannelConnectRefusedException : Exception
{
    public ChannelConnectRefusedException(string message, bool isPermanent, Exception? innerException = null)
        : base(message, innerException)
        => IsPermanent = isPermanent;

    /// <summary>True when reconnecting cannot succeed until something changes out-of-band.</summary>
    public bool IsPermanent { get; }
}

/// <summary>What the client knows about itself at connect time, for the transport handshake.</summary>
public sealed class ChannelConnectContext
{
    /// <summary>The app's self-reported id.</summary>
    public required string AppId { get; init; }

    /// <summary>The informational version of the <c>Keincheck.Client</c> assembly the app links.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>
    /// The client's heartbeat interval, so a transport can size its own liveness deadline
    /// consistently rather than inventing a second, conflicting timeout.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>An established session: the channel plus the transport's liveness policy.</summary>
public sealed class ChannelSession
{
    /// <summary>The connected channel, ready for <see cref="MessageKind.Register"/>.</summary>
    public required PipeChannel Channel { get; init; }

    /// <summary>
    /// Tear the session down if no frame arrives within this window; <c>null</c> to wait
    /// indefinitely.
    /// </summary>
    /// <remarks>
    /// The local pipe wants <c>null</c>: a dead peer surfaces immediately as EOF, so an
    /// idle-read timeout would only add false positives. TCP has no such courtesy — a
    /// black-holed connection (the remote machine's wifi drops mid-session) looks exactly like a quiet
    /// one, and without a deadline the client would wait forever on a socket that is never
    /// going to answer.
    /// </remarks>
    public TimeSpan? ReadTimeout { get; init; }

    /// <summary>A short, non-secret description of the established session, for logs.</summary>
    public string Description { get; init; } = string.Empty;
}
