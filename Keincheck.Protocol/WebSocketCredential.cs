using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keincheck.Protocol;

/// <summary>
/// What an app needs to attach to one specific hub over a WebSocket: where it listens, and the
/// token it issued. Minted by <c>Keincheck.Hub --issue-websocket-token</c> and normally embedded
/// at build time, so a browser app is bound to the hub it was built against.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint travels with the token deliberately. The hub knows its own port and path; an app
/// that had to hardcode <c>ws://127.0.0.1:3100/ws</c> alongside a token would silently stop
/// working the moment someone changed <c>HubOptions.HttpPort</c>.
/// </para>
/// <para>
/// <b>This is not a secret in a browser.</b> WebAssembly assemblies are downloaded to every
/// visitor, so an embedded token is readable by anyone who can load the page — unlike a desktop
/// binary, which at least sits on the user's own machine. That is tolerable for a locally served
/// development app and is not for a publicly deployed one; see the hub's origin allowlist, which
/// is the check that still holds when the token has leaked.
/// </para>
/// </remarks>
public sealed class WebSocketCredential
{
    /// <summary>
    /// The manifest resource name the build embeds this under, and the client reads it back from.
    /// </summary>
    public const string ResourceName = "Keincheck.WebSocket.Credential";

    /// <summary>The environment variable holding a credential, for apps that supply one at runtime.</summary>
    public const string EnvironmentVariable = "KEINCHECK_WEBSOCKET";

    /// <summary>The hub's WebSocket endpoint, e.g. <c>ws://127.0.0.1:3100/ws</c>.</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The token the hub issued for this app.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>The label the token was issued under, so an operator can revoke it by name.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    /// <summary>Serializes to the on-disk / embedded form.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, s_json);

    /// <summary>
    /// Parses the on-disk / embedded form. Throws <see cref="FormatException"/> on anything that
    /// is not a usable credential, rather than returning a half-populated one that fails later
    /// at connect time with a worse message.
    /// </summary>
    public static WebSocketCredential Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("The WebSocket credential is empty.");

        WebSocketCredential? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<WebSocketCredential>(text);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The WebSocket credential is not valid JSON.", ex);
        }

        if (parsed is null)
            throw new FormatException("The WebSocket credential is empty.");
        if (string.IsNullOrWhiteSpace(parsed.Endpoint))
            throw new FormatException("The WebSocket credential has no endpoint.");
        if (string.IsNullOrWhiteSpace(parsed.Token))
            throw new FormatException("The WebSocket credential has no token.");
        if (!Uri.TryCreate(parsed.Endpoint, UriKind.Absolute, out _))
            throw new FormatException($"The WebSocket credential's endpoint '{parsed.Endpoint}' is not an absolute URI.");

        return parsed;
    }

    /// <summary>Reads a credential from a file.</summary>
    public static WebSocketCredential LoadFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Reads the credential the build embedded in <paramref name="assembly"/>, or null when
    /// there is none. A missing resource is the ordinary case for an app that was not built with
    /// enrollment on, so it is not an error.
    /// </summary>
    public static WebSocketCredential? FromAssembly(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Reads a credential from <c>KEINCHECK_WEBSOCKET</c> (the credential itself) or
    /// <c>KEINCHECK_WEBSOCKET_FILE</c> (a path to it), or null when neither is set.
    /// </summary>
    /// <remarks>
    /// Checked before the embedded resource by <c>WebSocketChannelConnector.FromCredential</c>,
    /// so a machine can point a build-enrolled app at a different hub without rebuilding it.
    /// </remarks>
    public static WebSocketCredential? FromEnvironment()
    {
        var inline = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(inline))
            return Parse(inline);

        var path = Environment.GetEnvironmentVariable(EnvironmentVariable + "_FILE");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return LoadFile(path);

        return null;
    }
}
