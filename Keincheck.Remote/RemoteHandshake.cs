using Keincheck.Protocol;

namespace Keincheck.Remote;

/// <summary>
/// The post-TLS Hello / Welcome / Rejected exchange that opens a remote session.
/// </summary>
/// <remarks>
/// <para>
/// It carries <b>no secret</b>. By the time it runs, TLS has already established who both
/// parties are — the client holds a hub-issued certificate and the hub holds one the client's
/// pinned CA vouches for. This exchange exists for two other reasons.
/// </para>
/// <para>
/// First, <b>capability negotiation</b>: a feature is only used when both peers advertised it,
/// so nothing is ever sent that the other end cannot parse.
/// </para>
/// <para>
/// Second, and more importantly, <b>a diagnosable refusal</b>. On the local pipe a version
/// mismatch throws into a generic handler that writes to <c>Debug</c> and drops the
/// connection, so the client observes a bare disconnect. That is survivable when both ends are
/// on your desk. Over a network — to a headless machine that may be somewhere else entirely —
/// "it just disconnects" is close to undebuggable, so every refusal names its reason before
/// the socket closes.
/// </para>
/// </remarks>
public static class RemoteHandshake
{
    /// <summary>
    /// How long the whole handshake may take. Short on purpose: until it completes, the peer
    /// is holding one of a bounded number of unauthenticated session slots.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The capabilities this build understands.</summary>
    public static readonly IReadOnlyList<string> SupportedCapabilities =
    [
        RemoteCapabilities.Brotli,
        RemoteCapabilities.BidirectionalHeartbeat,
    ];

    /// <summary>
    /// How often the hub sends a heartbeat on an established remote session.
    /// </summary>
    /// <remarks>
    /// A remote session can be idle for a long time — nobody is driving the app between tool
    /// calls — and TCP will not tell either end that a silent connection has died. These
    /// keep the link demonstrably alive in both directions.
    /// </remarks>
    public static readonly TimeSpan ServerHeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many hub heartbeats a client waits for before deciding the link is dead. Four,
    /// so a couple of lost packets on a marginal wifi link do not tear down a healthy session.
    /// </summary>
    public const int MissedHeartbeatsBeforeDrop = 4;

    /// <summary>The outcome of a successful handshake.</summary>
    public sealed record Result
    {
        /// <summary>The host label the hub assigned (the client certificate's common name).</summary>
        public required string Host { get; init; }

        /// <summary>The hub's build version, informational.</summary>
        public string? ServerVersion { get; init; }

        /// <summary>Capabilities <b>both</b> peers advertised, and therefore the only ones in play.</summary>
        public required IReadOnlyList<string> Capabilities { get; init; }

        /// <summary>Whether Brotli frame compression was agreed.</summary>
        public bool Compression => Capabilities.Contains(RemoteCapabilities.Brotli);

        /// <summary>
        /// Whether the hub will send heartbeats, and therefore whether the peer should send
        /// them / expect them on this session.
        /// </summary>
        public bool Heartbeat => Capabilities.Contains(RemoteCapabilities.BidirectionalHeartbeat);

        /// <summary>
        /// How often the hub says it will send a heartbeat; null when it will not. A client
        /// applies an idle-read deadline only when this is set, and derives it from THIS
        /// value rather than from its own send interval.
        /// </summary>
        public TimeSpan? ServerHeartbeat { get; init; }
    }

    /// <summary>Thrown when the hub refused the session, carrying the reason it gave.</summary>
    /// <remarks>
    /// Derives from <see cref="ChannelConnectRefusedException"/> so the transport-agnostic
    /// client loop can act on the verdict without referencing this assembly — which is what
    /// makes <see cref="IsRetryable"/> actually stop a doomed reconnect loop rather than
    /// merely describe one.
    /// </remarks>
    public sealed class RejectedException(string code, string? reason)
        : ChannelConnectRefusedException(
            $"The hub refused the session ({code}): {reason ?? "no detail given"}",
            isPermanent: !IsRetryableCode(code))
    {
        /// <summary>A stable code from <see cref="RejectReason"/>.</summary>
        public string Code { get; } = code;

        /// <summary>
        /// Whether retrying could ever succeed. A revoked credential or an unsupported version
        /// will not fix itself, so a client should stop rather than reconnect in a loop that
        /// only feeds the hub's rate limiter.
        /// </summary>
        public bool IsRetryable => IsRetryableCode(Code);

        // Anything not listed here is permanent, and a permanent verdict makes the client's
        // reconnect loop break for good rather than back off — so a code landing on the wrong
        // side of this list is the difference between "recovers on its own" and "needs the app
        // restarted". HandshakeTimeout belongs here despite reading like a failure: it is the
        // most transient condition of the whole set. A congested SSH tunnel, a wifi flap partway
        // through the TLS exchange, or a hub busy enough to miss the deadline once all produce
        // it, and every one of them clears by itself. Treating it as permanent meant a single
        // stalled handshake retired the client until someone restarted the app — the exact
        // opposite of the roaming-suit behaviour remote exists for.
        private static bool IsRetryableCode(string code) => code is RejectReason.TooManySessions
            or RejectReason.RateLimited
            or RejectReason.RemoteDisabled
            or RejectReason.HandshakeTimeout
            or RejectReason.Internal;
    }

    // ---------------------------------------------------------------- client side

    /// <summary>
    /// Sends Hello and awaits the hub's verdict. On success the channel's limits are widened
    /// and compression enabled if it was agreed.
    /// </summary>
    /// <exception cref="RejectedException">The hub refused, and said why.</exception>
    public static async Task<Result> ClientAsync(
        PipeChannel channel,
        string? clientVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        await channel.SendAsync(MessageKind.Hello, new HelloMessage
        {
            ProtocolVersion = ProtocolVersion.Current,
            MachineName = Environment.MachineName,
            ClientVersion = clientVersion,
            Capabilities = SupportedCapabilities,
        }, cancellationToken: deadline.Token).ConfigureAwait(false);

        var envelope = await channel.ReceiveAsync(deadline.Token).ConfigureAwait(false)
            ?? throw new IOException("The hub closed the connection during the handshake.");

        if (envelope.Kind == MessageKind.Rejected)
        {
            var rejected = envelope.Unwrap<RejectedMessage>();
            throw new RejectedException(rejected?.Code ?? RejectReason.Internal, rejected?.Reason);
        }

        if (envelope.Kind != MessageKind.Welcome)
            throw new ProtocolException($"Expected Welcome or Rejected from the hub, got {envelope.Kind}.");

        var welcome = envelope.Unwrap<WelcomeMessage>()
            ?? throw new ProtocolException("The hub's Welcome was empty.");

        if (!ProtocolVersion.IsCompatible(welcome.ProtocolVersion))
            throw new ProtocolException(
                $"The hub speaks protocol v{welcome.ProtocolVersion}; this client supports " +
                $"[{ProtocolVersion.Minimum}, {ProtocolVersion.Current}].");

        var agreed = Intersect(welcome.Capabilities);

        // Only now does the channel get full-size framing. Compression is enabled only if the
        // hub echoed it back, so an older hub never receives a frame it cannot inflate.
        channel.Limits = ChannelLimits.Default with
        {
            AllowCompression = agreed.Contains(RemoteCapabilities.Brotli),
        };

        return new Result
        {
            Host = welcome.Host,
            ServerVersion = welcome.ServerVersion,
            Capabilities = agreed,
            // Only trust an interval the hub actually stated AND that the capability covers.
            ServerHeartbeat = agreed.Contains(RemoteCapabilities.BidirectionalHeartbeat)
                              && welcome.HeartbeatIntervalMs is > 0
                ? TimeSpan.FromMilliseconds(welcome.HeartbeatIntervalMs.Value)
                : null,
        };
    }

    // ---------------------------------------------------------------- server side

    /// <summary>
    /// Reads the peer's Hello and either welcomes it as <paramref name="assignedHost"/> or
    /// refuses it. Returns null when the session was refused (the caller then closes).
    /// </summary>
    /// <param name="vetoReason">
    /// A <see cref="RejectReason"/> code to refuse with regardless of the Hello's contents —
    /// used for a revoked credential or a disabled listener, which the caller establishes
    /// before this runs.
    /// </param>
    public static async Task<Result?> ServerAsync(
        PipeChannel channel,
        string assignedHost,
        string? serverVersion,
        string? vetoReason = null,
        string? vetoDetail = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            var envelope = await channel.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            if (envelope is null)
                return null; // peer went away mid-handshake; nothing to tell it

            if (vetoReason is not null)
            {
                await RejectAsync(channel, vetoReason, vetoDetail, deadline.Token).ConfigureAwait(false);
                return null;
            }

            if (envelope.Kind != MessageKind.Hello)
            {
                // Notably this refuses EnrollRequest: credential issuance is pipe-only, because
                // a remote peer able to mint further credentials would turn one leaked build
                // certificate into an unbounded grant.
                await RejectAsync(channel, RejectReason.NotPermittedOnTransport,
                    $"A remote session must open with Hello; got {envelope.Kind}.", deadline.Token).ConfigureAwait(false);
                return null;
            }

            var hello = envelope.Unwrap<HelloMessage>();
            if (hello is null)
            {
                await RejectAsync(channel, RejectReason.Internal, "The Hello payload was empty.", deadline.Token)
                    .ConfigureAwait(false);
                return null;
            }

            if (!ProtocolVersion.IsCompatible(hello.ProtocolVersion))
            {
                await RejectAsync(channel, RejectReason.VersionUnsupported,
                    $"Client speaks protocol v{hello.ProtocolVersion}; this hub supports " +
                    $"[{ProtocolVersion.Minimum}, {ProtocolVersion.Current}].", deadline.Token).ConfigureAwait(false);
                return null;
            }

            var agreed = Intersect(hello.Capabilities);
            var heartbeat = agreed.Contains(RemoteCapabilities.BidirectionalHeartbeat)
                ? ServerHeartbeatInterval
                : (TimeSpan?)null;

            await channel.SendAsync(MessageKind.Welcome, new WelcomeMessage
            {
                ProtocolVersion = ProtocolVersion.Current,
                Host = assignedHost,
                ServerVersion = serverVersion,
                Capabilities = agreed,
                // Promising this obliges the caller to actually run the pump -- see
                // RemoteClientListener, which starts it immediately after this returns.
                HeartbeatIntervalMs = heartbeat is { } h ? (int)h.TotalMilliseconds : null,
            }, cancellationToken: deadline.Token).ConfigureAwait(false);

            channel.Limits = ChannelLimits.Default with
            {
                AllowCompression = agreed.Contains(RemoteCapabilities.Brotli),
            };

            return new Result
            {
                Host = assignedHost,
                ServerVersion = serverVersion,
                Capabilities = agreed,
                ServerHeartbeat = heartbeat,
            };
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Best-effort courtesy: a peer that stalled may still be listening, and knowing it
            // ran out of time is more useful than a silent close. Never block on it.
            await TryRejectAsync(channel, RejectReason.HandshakeTimeout,
                $"The handshake did not complete within {Timeout.TotalSeconds:0}s.").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Tells the peer why it is being refused, then leaves the caller to close.</summary>
    public static Task RejectAsync(
        PipeChannel channel, string code, string? reason, CancellationToken cancellationToken = default)
        => channel.SendAsync(MessageKind.Rejected, new RejectedMessage { Code = code, Reason = reason },
            cancellationToken: cancellationToken);

    /// <summary>A rejection that must never itself throw — the connection is going away regardless.</summary>
    public static async Task TryRejectAsync(PipeChannel channel, string code, string? reason)
    {
        try
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await RejectAsync(channel, code, reason, brief.Token).ConfigureAwait(false);
        }
        catch
        {
            // The peer is already gone, or was never really there.
        }
    }

    private static IReadOnlyList<string> Intersect(IReadOnlyList<string>? peerCapabilities)
    {
        if (peerCapabilities is null || peerCapabilities.Count == 0)
            return [];

        var agreed = new List<string>(peerCapabilities.Count);
        foreach (var capability in SupportedCapabilities)
        {
            if (peerCapabilities.Contains(capability, StringComparer.Ordinal))
                agreed.Add(capability);
        }
        return agreed;
    }
}
