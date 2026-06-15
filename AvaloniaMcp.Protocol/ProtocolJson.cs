using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaloniaMcp.Protocol;

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
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
