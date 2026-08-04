namespace Keincheck.Protocol;

/// <summary>
/// The per-channel framing bounds a <see cref="PipeChannel"/> enforces on reads.
/// </summary>
/// <remarks>
/// <para>
/// These used to be fixed at <see cref="FrameCodec"/>'s defaults for every channel, which is
/// fine on the local control pipe — that pipe is <c>PipeOptions.CurrentUserOnly</c>, so the
/// only peers able to reach it could already do worse things directly. It is <b>not</b> fine
/// on a network listener: an unauthenticated peer that has merely completed a TCP connect
/// could otherwise drive the hub into reassembling up to
/// <see cref="FrameCodec.DefaultMaxMessageSize"/> (32 MiB) per connection, before anything
/// has established who it is.
/// </para>
/// <para>
/// So a remote channel starts at <see cref="Handshake"/> and is only widened to
/// <see cref="Default"/> once the session is accepted. Real payloads — screenshots, large
/// visual trees — only ever flow after that point.
/// </para>
/// </remarks>
public sealed record ChannelLimits
{
    /// <summary>The maximum payload bytes accepted in a single wire chunk.</summary>
    public required int MaxChunkPayload { get; init; }

    /// <summary>
    /// The maximum bytes of a single reassembled message. For a compressed frame this bounds
    /// the <i>decompressed</i> size, checked as it is produced.
    /// </summary>
    public required int MaxMessageSize { get; init; }

    /// <summary>
    /// Whether this channel may emit Brotli-compressed frames. Off until both peers have
    /// advertised <see cref="RemoteCapabilities.Brotli"/>, so a v1 peer never receives one.
    /// Reading a compressed frame is always supported; only writing is gated.
    /// </summary>
    public bool AllowCompression { get; init; }

    /// <summary>The full-size limits used for an established session and for the local pipe.</summary>
    public static readonly ChannelLimits Default = new()
    {
        MaxChunkPayload = FrameCodec.DefaultMaxChunkPayload,
        MaxMessageSize = FrameCodec.DefaultMaxMessageSize,
    };

    /// <summary>
    /// The clamp applied to a remote channel until its session is accepted. Generous for a
    /// Hello (a few hundred bytes) and far too small to be worth attacking.
    /// </summary>
    public static readonly ChannelLimits Handshake = new()
    {
        MaxChunkPayload = 8 * 1024,
        MaxMessageSize = 16 * 1024,
    };
}
