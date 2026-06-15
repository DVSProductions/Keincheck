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
    /// <summary>Unset / unknown. Treated as a protocol error.</summary>
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
