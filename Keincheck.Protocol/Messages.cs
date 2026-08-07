using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keincheck.Protocol;

/// <summary>
/// The kind of an IPC message on the broker channel. Carried in
/// <see cref="MessageEnvelope.Kind"/> so a receiver can dispatch without first
/// deserializing the full payload.
/// </summary>
public enum MessageKind
{
    /// <summary>
    /// Unset, or a kind this build does not know. Receivers <b>ignore</b> it and keep the
    /// session alive: a newer peer adding a kind must not be able to kill an older peer.
    /// </summary>
    Unknown = 0,

    /// <summary>Client → Hub: a host app announces itself and its protocol version (<see cref="RegisterMessage"/>).</summary>
    Register = 1,

    /// <summary>Hub → Client and Client → Hub: liveness ping (<see cref="HeartbeatMessage"/>).</summary>
    Heartbeat = 2,

    /// <summary>Client → Hub: the set of tools a client exposes (<see cref="ToolListMessage"/>).</summary>
    ToolList = 3,

    /// <summary>Hub → Client: invoke a tool with arguments (<see cref="InvokeToolMessage"/>).</summary>
    InvokeTool = 4,

    /// <summary>Client → Hub: the result (or error) of an invocation (<see cref="ToolResultMessage"/>).</summary>
    ToolResult = 5,

    /// <summary>Client → Hub (or Hub → subscribers): a client has gone away (<see cref="ClientDownMessage"/>).</summary>
    ClientDown = 6,

    // ---- v2: the remote-session handshake -------------------------------------------
    // Remote sessions run this exchange BEFORE Register. The local pipe never requires it,
    // which is what keeps v1 clients working unchanged.

    /// <summary>Client → Hub: opens a remote session and negotiates capabilities (<see cref="HelloMessage"/>).</summary>
    Hello = 7,

    /// <summary>Hub → Client: the session is accepted; carries the hub-assigned host (<see cref="WelcomeMessage"/>).</summary>
    Welcome = 8,

    /// <summary>
    /// Hub → Client: the session is refused, with a machine-readable reason
    /// (<see cref="RejectedMessage"/>). Sent before closing so a remote peer is never left
    /// guessing at a bare disconnect the way a v1 version-mismatch left it.
    /// </summary>
    Rejected = 9,

    // ---- v2: credential enrollment --------------------------------------------------
    // Pipe-only by construction. The control pipe is PipeOptions.CurrentUserOnly, so the OS
    // has already established that the requester is this user; that is the authorization.
    // These kinds are refused outright on a remote transport.

    /// <summary>Client → Hub, pipe only: request a remote client credential (<see cref="EnrollRequestMessage"/>).</summary>
    EnrollRequest = 10,

    /// <summary>Hub → Client, pipe only: the issued credential bundle, or a refusal (<see cref="EnrollResponseMessage"/>).</summary>
    EnrollResponse = 11,
}

/// <summary>
/// Why the hub refused a remote session. Machine-readable so a client can decide whether
/// retrying could ever help (<see cref="RateLimited"/> yes, <see cref="Revoked"/> never).
/// </summary>
public static class RejectReason
{
    /// <summary>The peer's protocol version is outside the hub's supported range.</summary>
    public const string VersionUnsupported = "version_unsupported";

    /// <summary>The presented credential has been revoked by the hub operator.</summary>
    public const string Revoked = "revoked";

    /// <summary>Remote access is not currently enabled on this hub.</summary>
    public const string RemoteDisabled = "remote_disabled";

    /// <summary>Too many sessions are already open.</summary>
    public const string TooManySessions = "too_many_sessions";

    /// <summary>This peer has failed too often, too recently.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The peer did not complete the handshake within the deadline.</summary>
    public const string HandshakeTimeout = "handshake_timeout";

    /// <summary>A message kind that is only valid on the local pipe arrived on a remote transport.</summary>
    public const string NotPermittedOnTransport = "not_permitted_on_transport";

    /// <summary>Something went wrong hub-side; the reason string carries detail.</summary>
    public const string Internal = "internal";
}

/// <summary>
/// Capability tokens peers exchange in <see cref="HelloMessage.Capabilities"/> /
/// <see cref="WelcomeMessage.Capabilities"/>. A capability is only used when <b>both</b>
/// sides advertised it, so an older peer is never sent something it cannot parse.
/// </summary>
public static class RemoteCapabilities
{
    /// <summary>Brotli-compressed frames (<c>FrameCodec</c> flag bit 1).</summary>
    public const string Brotli = "brotli";

    /// <summary>The peer accepts hub → client heartbeats (used to detect a black-holed link).</summary>
    public const string BidirectionalHeartbeat = "hb2";
}

/// <summary>
/// The outer envelope every broker message is wrapped in. It carries the
/// discriminator (<see cref="Kind"/>), the protocol version, and the raw JSON
/// <see cref="Payload"/> of the concrete message DTO. Serialize an envelope to
/// UTF-8 JSON, then hand the bytes to <c>FrameCodec.Write</c>; on the receiving
/// side, <c>FrameCodec.TryReadAsync</c> yields the bytes and you deserialize the
/// envelope, switch on <see cref="Kind"/>, and read the strongly-typed payload.
/// </summary>
public sealed class MessageEnvelope
{
    /// <summary>The message discriminator.</summary>
    [JsonPropertyName("kind")]
    public MessageKind Kind { get; set; } = MessageKind.Unknown;

    /// <summary>
    /// The protocol version of the sender (defaults to <see cref="ProtocolVersion.Current"/>).
    /// </summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = ProtocolVersion.Current;

    /// <summary>
    /// Correlation id linking a request to its response (e.g. an
    /// <see cref="InvokeToolMessage"/> to its <see cref="ToolResultMessage"/>).
    /// Optional for fire-and-forget messages.
    /// </summary>
    [JsonPropertyName("id")]
    public string? CorrelationId { get; set; }

    /// <summary>The raw JSON payload of the concrete message DTO for <see cref="Kind"/>.</summary>
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    /// <summary>
    /// Wraps a strongly-typed <paramref name="message"/> in an envelope, serializing
    /// it to a <see cref="JsonElement"/> payload and stamping the kind/version/id.
    /// </summary>
    public static MessageEnvelope Wrap<T>(
        MessageKind kind, T message, string? correlationId = null,
        JsonSerializerOptions? options = null)
    {
        var payload = JsonSerializer.SerializeToElement(message, options ?? ProtocolJson.Options);
        return new MessageEnvelope
        {
            Kind = kind,
            Version = ProtocolVersion.Current,
            CorrelationId = correlationId,
            Payload = payload,
        };
    }

    /// <summary>
    /// Deserializes <see cref="Payload"/> as <typeparamref name="T"/>. Returns
    /// <c>null</c> if the payload is absent or does not match.
    /// </summary>
    public T? Unwrap<T>(JsonSerializerOptions? options = null)
    {
        if (Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;
        return Payload.Deserialize<T>(options ?? ProtocolJson.Options);
    }
}

/// <summary>
/// Client → Hub. A host application (with an embedded Keincheck instance)
/// announces itself when it connects to the broker.
/// </summary>
public sealed class RegisterMessage
{
    /// <summary>Stable id the client picks for itself for the session.</summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Human-readable name (e.g. the host process / app title).</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>The OS process id of the host application, when known.</summary>
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    /// <summary>The protocol version the client speaks (for the handshake check).</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Keincheck.Protocol.ProtocolVersion.Current;

    /// <summary>
    /// True if the client owns one or more top-level windows at registration time (it is
    /// the UI-owning process). Lets the hub disambiguate the window-owner when one app
    /// launch produces several registrants. Recomputed on each <see cref="ToolListMessage"/>
    /// since windows usually open after startup. Defaults to false.
    /// </summary>
    [JsonPropertyName("ownsWindows")]
    public bool OwnsWindows { get; set; }

    /// <summary>
    /// The informational version of the Keincheck.Client assembly the host app is built
    /// against (e.g. "0.5.0"). Best-effort and purely informational — surfaced by the hub
    /// so an operator can see which client build each app links. Null when the client
    /// cannot determine its own version. Defaults to null.
    /// </summary>
    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }

    /// <summary>
    /// Echo of the <c>KEINCHECK_LAUNCH_TOKEN</c> environment variable, when the hub started
    /// this process on an agent's behalf. Lets the hub match the registration back to the
    /// launch that asked for it, and hand the instance to that agent alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Process id alone is not enough. An app is routinely started through a launcher — a
    /// shell script, a <c>dotnet</c> host, a shim — so the process the hub started is often
    /// not the process that registers, and pid matching silently fails exactly there.
    /// </para>
    /// <para>
    /// Optional and additive, so it needs no protocol-version bump: a client that predates
    /// the field simply never sends it, and the hub falls back to matching on process id.
    /// </para>
    /// </remarks>
    [JsonPropertyName("launchToken")]
    public string? LaunchToken { get; set; }
}

/// <summary>
/// Bidirectional liveness ping. The sender stamps a monotonically increasing
/// <see cref="Sequence"/> and a UTC timestamp; the peer may echo it back.
/// </summary>
public sealed class HeartbeatMessage
{
    /// <summary>The id of the client this heartbeat concerns.</summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Monotonic sequence number for ordering / loss detection.</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    /// <summary>UTC send time, in Unix milliseconds.</summary>
    [JsonPropertyName("timestampUnixMs")]
    public long TimestampUnixMs { get; set; }
}

/// <summary>A single tool a client exposes, mirrored to the hub's catalog.</summary>
public sealed class ToolDescriptor
{
    /// <summary>The tool's invocation name (unique within a client).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description shown to the model.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// The tool's JSON-Schema input definition (raw JSON). Optional; absent means
    /// "no declared schema".
    /// </summary>
    [JsonPropertyName("inputSchema")]
    public JsonElement? InputSchema { get; set; }

    /// <summary>
    /// Whether the client considers this tool free of side effects, from its MCP
    /// <c>ReadOnlyHint</c> annotation. The hub uses this to decide which tools a read-only
    /// client may still run.
    /// </summary>
    /// <remarks>
    /// <c>null</c> means "the client did not say" — either a v1 client, or a tool with no
    /// annotation. The hub then falls back to its own (fail-closed) name heuristic. The
    /// client is the right authority here: it owns the tool implementations and already
    /// computes this to enforce its own local read-only gate. Before this field existed the
    /// hub guessed from names alone and refused genuinely read-only tools such as
    /// <c>describe_screen</c> and <c>wait_for_idle</c> — which matters far more now that
    /// remote clients are read-only by default.
    /// </remarks>
    [JsonPropertyName("readOnly")]
    public bool? ReadOnly { get; set; }
}

/// <summary>
/// Client → Hub. The complete set of tools a client currently exposes. Sent on
/// registration and whenever the set changes.
/// </summary>
public sealed class ToolListMessage
{
    /// <summary>The id of the client these tools belong to.</summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The tools, in catalog order.</summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<ToolDescriptor> Tools { get; set; } = Array.Empty<ToolDescriptor>();

    /// <summary>
    /// True if the client owns one or more top-level windows at the time this list was
    /// sent. Recomputed on every tool-list so the hub's <c>ownsWindows</c> view stays
    /// fresh as windows open after startup. Defaults to false.
    /// </summary>
    [JsonPropertyName("ownsWindows")]
    public bool OwnsWindows { get; set; }
}

/// <summary>
/// Hub → Client. Requests that the client invoke one of its tools. The matching
/// <see cref="ToolResultMessage"/> carries the same correlation id (on the
/// envelope).
/// </summary>
public sealed class InvokeToolMessage
{
    /// <summary>The id of the client that owns the tool.</summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The tool name to invoke.</summary>
    [JsonPropertyName("toolName")]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>The invocation arguments as a raw JSON object. Absent = no args.</summary>
    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}

/// <summary>
/// Client → Hub. The outcome of an <see cref="InvokeToolMessage"/>. Either
/// <see cref="IsError"/> is false and <see cref="Content"/> holds the JSON
/// result (which may include base64 image blocks for screenshots), or
/// <see cref="IsError"/> is true and <see cref="Error"/> describes the failure.
/// </summary>
public sealed class ToolResultMessage
{
    /// <summary>The id of the client that produced this result.</summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The tool name that was invoked.</summary>
    [JsonPropertyName("toolName")]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>True if the invocation failed; then <see cref="Error"/> is set.</summary>
    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    /// <summary>The successful result payload as raw JSON (MCP content blocks).</summary>
    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    /// <summary>A human-readable error message when <see cref="IsError"/> is true.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Client → Hub (or Hub → subscribers). Signals that a client connection has
/// ended — gracefully or because the transport dropped.
/// </summary>
public sealed class ClientDownMessage
{
    /// <summary>The id of the client that went away.</summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Optional human-readable reason (e.g. "app exited", "transport reset").</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>True if the shutdown was clean (the client said goodbye).</summary>
    [JsonPropertyName("graceful")]
    public bool Graceful { get; set; }
}

// ===================================================================== v2: remote session

/// <summary>
/// Client → Hub, remote transports only. The first frame of a remote session, sent after
/// the TLS handshake has already established <i>who</i> both parties are. Hello carries no
/// secret: authentication is the mutually-validated certificate, not anything in here.
/// </summary>
public sealed class HelloMessage
{
    /// <summary>The protocol version the client speaks.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Keincheck.Protocol.ProtocolVersion.Current;

    /// <summary>
    /// The client's own machine name. <b>Informational only</b> — self-reported and
    /// therefore not trustworthy. The authoritative host is the CN of the validated client
    /// certificate, which the hub stamps and returns in <see cref="WelcomeMessage.Host"/>.
    /// </summary>
    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    /// <summary>The informational version of the Keincheck.Client assembly the app links.</summary>
    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }

    /// <summary>Optional features the client can use (see <see cref="RemoteCapabilities"/>).</summary>
    [JsonPropertyName("caps")]
    public IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Hub → Client, remote transports only. Accepts the session and tells the client the host
/// label the hub will file it under, so its own logs match what the operator sees.
/// </summary>
public sealed class WelcomeMessage
{
    /// <summary>The protocol version the hub speaks.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Keincheck.Protocol.ProtocolVersion.Current;

    /// <summary>
    /// The host label assigned to this session — the CN of the client certificate the hub
    /// just validated. The client's hub id becomes <c>AppId@Host#n</c>.
    /// </summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    /// <summary>The hub's informational build version.</summary>
    [JsonPropertyName("serverVersion")]
    public string? ServerVersion { get; set; }

    /// <summary>
    /// The subset of the client's advertised capabilities the hub also supports. Only these
    /// may be used; anything else stays off.
    /// </summary>
    [JsonPropertyName("caps")]
    public IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();

    /// <summary>
    /// How often the hub will send a heartbeat on this session, in milliseconds, when
    /// <see cref="RemoteCapabilities.BidirectionalHeartbeat"/> was agreed. Null means the hub
    /// sends none, and the client must not apply an idle-read deadline.
    /// </summary>
    /// <remarks>
    /// The hub states this rather than both ends assuming a shared constant. A client that
    /// derived its deadline from its <i>own</i> heartbeat interval would tear the session down
    /// whenever the hub happened to be quieter than the client guessed — which is a silent,
    /// self-inflicted disconnect loop that looks exactly like a flaky network.
    /// </remarks>
    [JsonPropertyName("heartbeatMs")]
    public int? HeartbeatIntervalMs { get; set; }
}

/// <summary>
/// Hub → Client. The session is refused. Sent immediately before the hub closes the
/// connection so the peer learns <i>why</i> instead of seeing a bare disconnect.
/// </summary>
public sealed class RejectedMessage
{
    /// <summary>A stable machine-readable code (see <see cref="RejectReason"/>).</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = RejectReason.Internal;

    /// <summary>A human-readable explanation for logs.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

// ===================================================================== v2: enrollment

/// <summary>
/// Client → Hub, <b>local pipe only</b>. Asks the hub to issue a remote client credential.
/// The hub refuses this kind outright on a remote transport: a remote peer must never be
/// able to mint further credentials, or one leaked build cert would become an unbounded
/// grant. On the pipe, <c>PipeOptions.CurrentUserOnly</c> is the authorization.
/// </summary>
public sealed class EnrollRequestMessage
{
    /// <summary>
    /// The host label to issue for — becomes the certificate CN and the <c>@Host</c> in the
    /// client's hub id. Sanitized and length-limited by the hub.
    /// </summary>
    [JsonPropertyName("targetName")]
    public string TargetName { get; set; } = string.Empty;

    /// <summary>Free-text note recorded against the issued credential (e.g. the project that asked).</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Requested validity in days. The hub clamps this to its own policy maximum.</summary>
    [JsonPropertyName("requestedDays")]
    public int RequestedDays { get; set; }
}

/// <summary>Hub → Client, local pipe only. The issued credential, or why it was refused.</summary>
public sealed class EnrollResponseMessage
{
    /// <summary>True when <see cref="Bundle"/> holds a freshly-issued credential.</summary>
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    /// <summary>
    /// The opaque enrollment bundle (client certificate + private key + the hub's CA
    /// certificate). Treat as a secret: anything holding it can attach to this hub.
    /// </summary>
    [JsonPropertyName("bundle")]
    public string? Bundle { get; set; }

    /// <summary>The certificate serial, so the operator can match it to the revocation list.</summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    /// <summary>When the issued credential stops working.</summary>
    [JsonPropertyName("notAfter")]
    public DateTimeOffset? NotAfter { get; set; }

    /// <summary>Why the request was refused when <see cref="Accepted"/> is false.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
