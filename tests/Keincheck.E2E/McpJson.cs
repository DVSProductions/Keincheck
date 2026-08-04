using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Keincheck.E2E;

/// <summary>
/// Reading helpers for <see cref="CallToolResult"/>. Every hub meta-tool answers with a
/// single text block holding JSON; the client's own tools mostly do the same, and the
/// screenshot tools add an image block ahead of it.
/// </summary>
public static class McpJson
{
    /// <summary>All text blocks concatenated — the human-readable form of a result.</summary>
    public static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>
    /// Asserts the call succeeded and parses its last text block as JSON. The LAST block
    /// is the right one: <c>screenshot_marked</c> emits the image first and the legend
    /// after it, so taking the first text block would work by luck on meta-tools and
    /// silently mis-parse there.
    /// </summary>
    public static JsonElement Ok(CallToolResult result)
    {
        Assert.False(result.IsError ?? false, $"tool call reported an error: {Text(result)}");

        var text = result.Content.OfType<TextContentBlock>().LastOrDefault()?.Text;
        Assert.False(string.IsNullOrWhiteSpace(text), "tool call returned no text content to parse.");

        return JsonDocument.Parse(text!).RootElement.Clone();
    }

    /// <summary>Asserts the call FAILED and returns its message, for the refusal paths.</summary>
    public static string Refused(CallToolResult result)
    {
        var text = Text(result);
        Assert.True(result.IsError ?? false, $"expected a refusal but the call succeeded: {text}");
        return text;
    }

    /// <summary>The first image block, or null. Screenshot tools emit exactly one.</summary>
    public static ImageContentBlock? Image(CallToolResult result) =>
        result.Content.OfType<ImageContentBlock>().FirstOrDefault();

    // ---- element readers -------------------------------------------------

    public static string? Str(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    public static bool? Bool(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    public static JsonElement? Prop(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) ? v : null;

    /// <summary>Enumerates an array property, or nothing when it is absent or not an array.</summary>
    public static IEnumerable<JsonElement> Array(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    /// <summary>Writes a result to the artifacts directory so a failing run leaves evidence.</summary>
    public static void Save(string fileName, string content) =>
        File.WriteAllText(Path.Combine(E2EEnvironment.ArtifactsDirectory, fileName), content);

    /// <summary>Writes bytes (a decoded screenshot) to the artifacts directory.</summary>
    public static void Save(string fileName, byte[] content) =>
        File.WriteAllBytes(Path.Combine(E2EEnvironment.ArtifactsDirectory, fileName), content);
}
