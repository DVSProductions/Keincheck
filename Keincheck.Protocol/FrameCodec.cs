using System.Buffers.Binary;
using System.IO.Compression;

namespace Keincheck.Protocol;

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
///   <item>1-byte flags: bit0 = <c>final</c> (this is the last chunk of the frame),
///         bit1 = <c>compressed</c> (the reassembled frame is Brotli-compressed). Bits 2-7 are reserved.</item>
///   <item>4-byte big-endian <c>uint</c> chunk length <c>N</c> (the payload-byte count in this chunk).</item>
///   <item><c>N</c> payload bytes.</item>
/// </list>
/// <para>
/// The reader concatenates chunk payloads until it sees a chunk with the
/// <c>final</c> flag set, then returns the reassembled message. A message with an
/// empty payload is a single final chunk with <c>N == 0</c>.
/// </para>
/// <para><b>Compression.</b> The <c>compressed</c> flag is set on <i>every</i> chunk of a
/// compressed frame and must not change mid-frame. Compression is applied to the whole
/// payload <i>before</i> chunking, so both the on-wire size and the reassembled size stay
/// bounded — and the decompressed size is checked against the same cap as it is produced,
/// so a compression bomb is refused rather than allocated. A v1 peer never sets the flag
/// and never receives one (the caller only opts in after capability negotiation).</para>
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

    /// <summary>
    /// Payloads at or below this size are never compressed: Brotli's framing overhead
    /// makes small control messages (register, heartbeat, tool results) bigger, not smaller.
    /// </summary>
    public const int CompressionThreshold = 1024;

    private const byte FlagFinal = 0x01;
    private const byte FlagCompressed = 0x02;

    private static readonly byte[] MagicBytes =
        System.Text.Encoding.ASCII.GetBytes(ProtocolVersion.Magic);

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Writes <paramref name="payload"/> to <paramref name="stream"/> as one or
    /// more chunks (splitting at <paramref name="maxChunkPayload"/>) and flushes.
    /// Synchronous; suitable for small control messages.
    /// </summary>
    /// <remarks>
    /// Kept as the exact pre-v2 signature. Adding an optional parameter to it instead would
    /// have changed the signature callers are compiled against, so an assembly built against
    /// the published 0.9.0 package would fail with <c>MissingMethodException</c> at runtime —
    /// a source-compatible change that is not binary-compatible.
    /// </remarks>
    public static void Write(
        Stream stream,
        ReadOnlySpan<byte> payload,
        int maxChunkPayload = DefaultMaxChunkPayload)
        => Write(stream, payload, maxChunkPayload, compress: false);

    /// <summary>
    /// Writes <paramref name="payload"/> as one or more chunks, optionally Brotli-compressing
    /// the frame first. Only compress when the peer has advertised support for it.
    /// </summary>
    public static void Write(
        Stream stream,
        ReadOnlySpan<byte> payload,
        int maxChunkPayload,
        bool compress)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxChunkPayload <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChunkPayload));

        byte[]? compressed = null;
        if (TryCompress(payload, compress, out compressed))
            payload = compressed;

        var header = new byte[ChunkHeaderSize];
        var offset = 0;
        var total = payload.Length;

        // Always emit at least one chunk (an empty payload => one empty final chunk).
        do
        {
            var take = Math.Min(maxChunkPayload, total - offset);
            var isFinal = offset + take >= total;
            WriteChunkHeader(header, isFinal, compressed is not null, take);
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
    /// <remarks>
    /// Kept as the exact pre-v2 signature for binary compatibility with the published package;
    /// see the note on the synchronous <see cref="Write(Stream, ReadOnlySpan{byte}, int)"/>.
    /// </remarks>
    public static Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        int maxChunkPayload = DefaultMaxChunkPayload,
        CancellationToken cancellationToken = default)
        => WriteAsync(stream, payload, maxChunkPayload, compress: false, cancellationToken);

    /// <summary>
    /// Writes <paramref name="payload"/> as one or more chunks, optionally Brotli-compressing
    /// the frame first. Only compress when the peer has advertised support for it.
    /// </summary>
    public static async Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        int maxChunkPayload,
        bool compress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxChunkPayload <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChunkPayload));

        if (TryCompress(payload.Span, compress, out var compressed))
            payload = compressed;

        var header = new byte[ChunkHeaderSize];
        var offset = 0;
        var total = payload.Length;

        do
        {
            var take = Math.Min(maxChunkPayload, total - offset);
            var isFinal = offset + take >= total;
            WriteChunkHeader(header, isFinal, compressed is not null, take);
            await stream.WriteAsync(header.AsMemory(0, header.Length), cancellationToken).ConfigureAwait(false);
            if (take > 0)
                await stream.WriteAsync(payload.Slice(offset, take), cancellationToken).ConfigureAwait(false);
            offset += take;
        }
        while (offset < total);

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteChunkHeader(byte[] header, bool isFinal, bool isCompressed, int chunkLength)
    {
        // [0..4) magic, [4] flags, [5..9) length (big-endian uint).
        MagicBytes.CopyTo(header, 0);
        var flags = (byte)0;
        if (isFinal) flags |= FlagFinal;
        if (isCompressed) flags |= FlagCompressed;
        header[4] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), (uint)chunkLength);
    }

    // ---------------------------------------------------------------- compression

    /// <summary>
    /// Compresses <paramref name="payload"/> when the caller opted in, the payload is worth
    /// compressing, and the result is actually smaller. Returns false (leaving the caller on
    /// the uncompressed path) otherwise — so an incompressible payload never pays a size
    /// penalty for trying.
    /// </summary>
    private static bool TryCompress(ReadOnlySpan<byte> payload, bool compress, out byte[]? compressed)
    {
        compressed = null;
        if (!compress || payload.Length <= CompressionThreshold)
            return false;

        using var output = new MemoryStream(payload.Length / 2);
        // Quality 1. The payloads that matter here are base64-encoded PNGs, which this
        // strips ~30% off almost instantly; Brotli's default quality 11 would cost seconds
        // on a multi-MiB screenshot for a few percent more.
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(payload);

        if (output.Length >= payload.Length)
            return false; // compression made it bigger — send the original

        compressed = output.ToArray();
        return true;
    }

    /// <summary>
    /// Inflates a Brotli frame, refusing to produce more than <paramref name="maxMessageSize"/>
    /// bytes. The bound is enforced <i>as output is produced</i>, not after, so a small
    /// hostile frame that would inflate to gigabytes is refused rather than allocated.
    /// </summary>
    private static byte[] Decompress(byte[] compressed, int maxMessageSize)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var buffer = new byte[Math.Min(64 * 1024, Math.Max(4096, maxMessageSize))];
        while (true)
        {
            int n;
            try
            {
                n = brotli.Read(buffer, 0, buffer.Length);
            }
            // The decoder's exception type is an implementation detail that is not stable
            // across runtimes (net8 raises InvalidOperationException here, and the docs
            // promise InvalidDataException), so normalise the family rather than one name.
            // A malformed frame must surface as a ProtocolException the transport already
            // knows to treat as fatal-for-the-connection.
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
            {
                throw new ProtocolException("Compressed frame is not valid Brotli data.", ex);
            }

            if (n <= 0)
                break;

            if (output.Length + n > maxMessageSize)
                throw new ProtocolException(
                    $"Decompressed message would exceed the maximum of {maxMessageSize} bytes.");

            output.Write(buffer, 0, n);
        }

        return output.ToArray();
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
        var isCompressed = false;

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
                    throw new ProtocolException("Frame magic mismatch; stream is not aligned to an Keincheck frame.");
            }

            var isFinal = (header[4] & FlagFinal) != 0;
            var chunkCompressed = (header[4] & FlagCompressed) != 0;
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));

            // The compressed flag describes the whole frame, so it must be identical on
            // every chunk. A frame that flips it mid-stream is malformed, not merely odd.
            if (first)
                isCompressed = chunkCompressed;
            else if (chunkCompressed != isCompressed)
                throw new ProtocolException("Chunk compression flag changed mid-frame.");

            if (chunkLength > (uint)maxChunkPayload)
                throw new ProtocolException($"Chunk length {chunkLength} exceeds the maximum of {maxChunkPayload} bytes.");

            // An empty non-final chunk is the one input both caps are blind to: it fails
            // `> maxChunkPayload` and it adds nothing to `assembled`, so a peer can stream
            // them forever at 9 bytes each and neither bound can ever fire. The writer above
            // cannot produce one (take == 0 only when total == 0, which sets isFinal), and the
            // wire format defines the empty message as a single FINAL chunk — so this carries
            // no data any well-formed frame could need. Every non-zero length is already
            // bounded: the frame grows, so maxMessageSize terminates it.
            if (chunkLength == 0 && !isFinal)
                throw new ProtocolException("Empty non-final chunk; a frame cannot be padded with data-less chunks.");

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
            {
                var bytes = assembled.ToArray();
                return isCompressed ? Decompress(bytes, maxMessageSize) : bytes;
            }
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
