using System.Buffers.Binary;

namespace AvaloniaMcp.Protocol;

/// <summary>
/// A length-prefixed, <b>chunked</b> framing codec over a byte <see cref="Stream"/>.
/// A logical message (a "frame") is written as one or more wire chunks so that an
/// arbitrarily large payload — e.g. a base64 PNG screenshot — can be transmitted
/// without buffering the whole thing as a single oversized length prefix, and so a
/// reader can enforce a per-chunk bound while still reassembling the full message.
/// </summary>
/// <remarks>
/// <para><b>Wire format.</b> Each frame is a sequence of chunks. Every chunk is:</para>
/// <list type="number">
///   <item>4-byte big-endian magic <c>"AMCP"</c> (<see cref="ProtocolVersion.Magic"/>) — resync/sanity guard.</item>
///   <item>1-byte flags: bit0 = <c>final</c> (this is the last chunk of the frame).</item>
///   <item>4-byte big-endian <c>uint</c> chunk length <c>N</c> (the payload-byte count in this chunk).</item>
///   <item><c>N</c> payload bytes.</item>
/// </list>
/// <para>
/// The reader concatenates chunk payloads until it sees a chunk with the
/// <c>final</c> flag set, then returns the reassembled message. A message with an
/// empty payload is a single final chunk with <c>N == 0</c>.
/// </para>
/// <para>
/// The codec is transport-agnostic (works over a named pipe, TCP
/// <c>NetworkStream</c>, or an in-memory <see cref="MemoryStream"/>) and is fully
/// unit-testable against a round-trip through a single stream.
/// </para>
/// </remarks>
public static class FrameCodec
{
    /// <summary>Bytes of fixed per-chunk overhead: 4 magic + 1 flags + 4 length.</summary>
    public const int ChunkHeaderSize = 9;

    /// <summary>The default maximum payload bytes carried in a single wire chunk (64 KiB).</summary>
    public const int DefaultMaxChunkPayload = 64 * 1024;

    /// <summary>
    /// A hard cap on a single reassembled message (32 MiB) so a malicious or buggy
    /// peer cannot drive the reader into unbounded allocation. Large screenshots
    /// fit comfortably; adjust here if a legitimate payload ever needs more.
    /// </summary>
    public const int DefaultMaxMessageSize = 32 * 1024 * 1024;

    private const byte FlagFinal = 0x01;

    private static readonly byte[] MagicBytes =
        System.Text.Encoding.ASCII.GetBytes(ProtocolVersion.Magic);

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Writes <paramref name="payload"/> to <paramref name="stream"/> as one or
    /// more chunks (splitting at <paramref name="maxChunkPayload"/>) and flushes.
    /// Synchronous; suitable for small control messages.
    /// </summary>
    public static void Write(
        Stream stream,
        ReadOnlySpan<byte> payload,
        int maxChunkPayload = DefaultMaxChunkPayload)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxChunkPayload <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChunkPayload));

        var header = new byte[ChunkHeaderSize];
        var offset = 0;
        var total = payload.Length;

        // Always emit at least one chunk (an empty payload => one empty final chunk).
        do
        {
            var take = Math.Min(maxChunkPayload, total - offset);
            var isFinal = offset + take >= total;
            WriteChunkHeader(header, isFinal, take);
            stream.Write(header, 0, header.Length);
            if (take > 0)
                stream.Write(payload.Slice(offset, take));
            offset += take;
        }
        while (offset < total);

        stream.Flush();
    }

    /// <summary>
    /// Asynchronously writes <paramref name="payload"/> as one or more chunks and
    /// flushes. Prefer this over the synchronous overload on real network/pipe
    /// transports.
    /// </summary>
    public static async Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        int maxChunkPayload = DefaultMaxChunkPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxChunkPayload <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChunkPayload));

        var header = new byte[ChunkHeaderSize];
        var offset = 0;
        var total = payload.Length;

        do
        {
            var take = Math.Min(maxChunkPayload, total - offset);
            var isFinal = offset + take >= total;
            WriteChunkHeader(header, isFinal, take);
            await stream.WriteAsync(header.AsMemory(0, header.Length), cancellationToken).ConfigureAwait(false);
            if (take > 0)
                await stream.WriteAsync(payload.Slice(offset, take), cancellationToken).ConfigureAwait(false);
            offset += take;
        }
        while (offset < total);

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteChunkHeader(byte[] header, bool isFinal, int chunkLength)
    {
        // [0..4) magic, [4] flags, [5..9) length (big-endian uint).
        MagicBytes.CopyTo(header, 0);
        header[4] = isFinal ? FlagFinal : (byte)0x00;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), (uint)chunkLength);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Reads exactly one reassembled message (all its chunks up to and including
    /// the <c>final</c> chunk) from <paramref name="stream"/>. Returns the message
    /// bytes, or <c>null</c> on a clean end-of-stream <i>before any chunk header
    /// was read</i> (the peer closed the connection between frames).
    /// </summary>
    /// <exception cref="ProtocolException">
    /// The stream ended mid-frame, a chunk's magic did not match, or the
    /// reassembled message exceeded <paramref name="maxMessageSize"/>.
    /// </exception>
    public static async Task<byte[]?> TryReadAsync(
        Stream stream,
        int maxChunkPayload = DefaultMaxChunkPayload,
        int maxMessageSize = DefaultMaxMessageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var assembled = new MemoryStream();
        var header = new byte[ChunkHeaderSize];
        var first = true;

        while (true)
        {
            // Read the chunk header. A clean EOF on the FIRST byte of the FIRST
            // chunk header means "no more frames" -> null. EOF anywhere else is a
            // truncated frame -> error.
            var got = await ReadAtLeastAsync(stream, header, allowZero: first, cancellationToken).ConfigureAwait(false);
            if (got == 0 && first)
                return null;
            if (got < ChunkHeaderSize)
                throw new ProtocolException("Stream ended in the middle of a chunk header.");

            // Validate magic.
            for (var i = 0; i < MagicBytes.Length; i++)
            {
                if (header[i] != MagicBytes[i])
                    throw new ProtocolException("Frame magic mismatch; stream is not aligned to an AvaloniaMcp frame.");
            }

            var isFinal = (header[4] & FlagFinal) != 0;
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));

            if (chunkLength > (uint)maxChunkPayload)
                throw new ProtocolException($"Chunk length {chunkLength} exceeds the maximum of {maxChunkPayload} bytes.");

            if (assembled.Length + chunkLength > maxMessageSize)
                throw new ProtocolException($"Reassembled message would exceed the maximum of {maxMessageSize} bytes.");

            if (chunkLength > 0)
            {
                var buffer = new byte[chunkLength];
                var read = await ReadAtLeastAsync(stream, buffer, allowZero: false, cancellationToken).ConfigureAwait(false);
                if (read < buffer.Length)
                    throw new ProtocolException("Stream ended in the middle of a chunk payload.");
                assembled.Write(buffer, 0, buffer.Length);
            }

            first = false;
            if (isFinal)
                return assembled.ToArray();
        }
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> completely, looping over partial reads.
    /// Returns the number of bytes read; a return value less than the buffer
    /// length means end-of-stream was hit. When <paramref name="allowZero"/> is
    /// true and the very first read returns 0, returns 0 (clean EOF between frames).
    /// </summary>
    private static async Task<int> ReadAtLeastAsync(
        Stream stream, byte[] buffer, bool allowZero, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (offset == 0 && allowZero)
                    return 0; // clean EOF before any bytes of this (first) header
                return offset; // truncated
            }
            offset += n;
        }
        return offset;
    }
}
