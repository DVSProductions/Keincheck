using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keincheck.Protocol;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for all broker IPC messages, so
/// both ends of the channel serialize and deserialize DTOs identically. Enums
/// are written as strings (stable across versions), nulls are omitted, and
/// property names use the explicit <c>[JsonPropertyName]</c> attributes on the
/// DTOs (so casing is deterministic regardless of the runtime default).
/// </summary>
public static class ProtocolJson
{
    /// <summary>The canonical options instance. Treat as immutable.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };
        // Order matters: options.Converters is consulted in order and the first converter
        // that CanConvert wins, so the tolerant MessageKind reader must precede the general
        // enum converter (which claims every enum).
        options.Converters.Add(new TolerantMessageKindConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>
    /// Reads <see cref="MessageKind"/>, mapping any name this build does not know to
    /// <see cref="MessageKind.Unknown"/> instead of throwing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProtocolVersion"/> promises that protocol growth is additive and that an
    /// older peer simply ignores what it does not recognise. The stock
    /// <see cref="JsonStringEnumConverter"/> breaks that promise at the first hurdle: an
    /// unrecognised name raises <see cref="JsonException"/> out of
    /// <see cref="PipeChannel.ReceiveAsync"/>, which every production receive loop treats as
    /// fatal-for-the-connection. So the moment v3 introduces a kind and sends it to a v2 peer,
    /// that peer's session dies — the dispatch switches never even get to ignore it.
    /// </para>
    /// <para>
    /// Deserializing to <see cref="MessageKind.Unknown"/> restores the intended behaviour:
    /// the frame is consumed, no handler matches, and the session survives. Writing is
    /// unchanged — names only, never numbers.
    /// </para>
    /// </remarks>
    private sealed class TolerantMessageKindConverter : JsonConverter<MessageKind>
    {
        public override MessageKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var name = reader.GetString();
                    return Enum.TryParse<MessageKind>(name, ignoreCase: true, out var parsed)
                        && Enum.IsDefined(parsed)
                        ? parsed
                        : MessageKind.Unknown;

                // Not written by this codec, but a peer that sends the numeric form should be
                // tolerated on the same terms rather than being a second way to kill a session.
                case JsonTokenType.Number:
                    return reader.TryGetInt32(out var number) && Enum.IsDefined((MessageKind)number)
                        ? (MessageKind)number
                        : MessageKind.Unknown;

                case JsonTokenType.Null:
                    return MessageKind.Unknown;

                default:
                    // A structurally wrong token (an object, an array) is malformed JSON rather
                    // than a forward-compatibility question, so let it surface as one.
                    throw new JsonException($"Expected a string for {nameof(MessageKind)}, got {reader.TokenType}.");
            }
        }

        public override void Write(Utf8JsonWriter writer, MessageKind value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
