namespace Keincheck.Protocol;

/// <summary>
/// The well-known shape of the hub's client-attach WebSocket endpoint.
/// </summary>
/// <remarks>
/// Both ends read these from here for the same reason <see cref="PipeNames"/> exists: the hub
/// and the client are shipped as separate packages on independent versions, so a path or
/// parameter name spelled out twice is a thing that can silently drift into "it just stopped
/// connecting".
/// </remarks>
public static class WebSocketEndpoint
{
    /// <summary>The default path the hub serves client attachments on.</summary>
    public const string DefaultPath = "/ws";

    /// <summary>
    /// The query parameter carrying the hub-issued token.
    /// </summary>
    /// <remarks>
    /// A query parameter rather than an <c>Authorization</c> header because the browser's
    /// WebSocket API exposes no way to set request headers, and the browser is the whole reason
    /// this transport exists. Query strings can reach server logs, so the hub's tokens are
    /// per-label, revocable, and only ever valid against the loopback endpoint that issued them.
    /// </remarks>
    public const string TokenQueryParameter = "token";

    /// <summary>
    /// The query parameter carrying the app's self-reported id. A hint for the hub's logs and
    /// consent UI only — the authoritative id still arrives in <see cref="RegisterMessage"/>.
    /// </summary>
    public const string AppIdQueryParameter = "appId";
}
