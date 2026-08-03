using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// Tests for the v2 Brotli frame-compression flag (<c>FrameCodec</c> header bit 1).
/// The important guarantees are not "it makes things smaller" but the safety ones: a v1
/// peer must never be handed a compressed frame, and a hostile peer must not be able to
/// spend a few hundred bytes to make the reader allocate gigabytes.
/// </summary>
public class FrameCompressionTests
{
    /// <summary>Highly compressible, but not trivially so — mirrors base64 text.</summary>
    private static byte[] Compressible(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(alphabet[i % alphabet.Length]);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Incompressible(int length, int seed = 7)
    {
        var data = new byte[length];
        var x = (uint)(seed * 2654435761u + 1u);
        for (var i = 0; i < length; i++)
        {
            x = x * 1103515245u + 12345u;
            data[i] = (byte)(x >> 16);
        }
        return data;
    }

    private static bool FirstChunkIsCompressed(byte[] framed) => (framed[4] & 0x02) != 0;

    // ---------------------------------------------------------------- round-trip

    [Fact]
    public async Task Compressed_Frame_RoundTrips_Identically()
    {
        var payload = Compressible(200_000);
        using var ms = new MemoryStream();

        await FrameCodec.WriteAsync(ms, payload, compress: true);
        Assert.True(FirstChunkIsCompressed(ms.ToArray()));
        Assert.True(ms.Length < payload.Length / 2, "compressible payload should shrink a lot");

        ms.Position = 0;
        var read = await FrameCodec.TryReadAsync(ms);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Compressed_Frame_Spanning_ManyChunks_RoundTrips()
    {
        // Big enough that the COMPRESSED bytes still need several chunks, so the
        // per-chunk flag has to stay consistent across the whole frame.
        var payload = Compressible(4 * 1024 * 1024);
        using var ms = new MemoryStream();

        await FrameCodec.WriteAsync(ms, payload, maxChunkPayload: 4096, compress: true);

        ms.Position = 0;
        var read = await FrameCodec.TryReadAsync(ms, maxChunkPayload: 4096);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Sync_Writer_Also_Compresses_And_Matches_Async()
    {
        var payload = Compressible(50_000);

        using var sync = new MemoryStream();
        FrameCodec.Write(sync, payload, compress: true);

        using var async = new MemoryStream();
        await FrameCodec.WriteAsync(async, payload, compress: true);

        Assert.Equal(sync.ToArray(), async.ToArray());
    }

    // ---------------------------------------------------------------- opt-in only

    [Fact]
    public async Task Without_OptIn_Nothing_Is_Compressed()
    {
        // This is the v1-compatibility guarantee: the flag bit is only ever set when the
        // caller asked, and the caller only asks after both peers advertised the capability.
        var payload = Compressible(200_000);
        using var ms = new MemoryStream();

        await FrameCodec.WriteAsync(ms, payload);

        var framed = ms.ToArray();
        Assert.False(FirstChunkIsCompressed(framed));
        Assert.True(ms.Length > payload.Length, "uncompressed framing only adds chunk headers");

        ms.Position = 0;
        Assert.Equal(payload, await FrameCodec.TryReadAsync(ms));
    }

    [Fact]
    public async Task Small_Payloads_Are_Never_Compressed()
    {
        // Brotli framing overhead makes a heartbeat BIGGER. Below the threshold we skip it.
        var payload = Compressible(FrameCodec.CompressionThreshold);
        using var ms = new MemoryStream();

        await FrameCodec.WriteAsync(ms, payload, compress: true);

        Assert.False(FirstChunkIsCompressed(ms.ToArray()));
        Assert.Equal(payload.Length + FrameCodec.ChunkHeaderSize, ms.Length);

        ms.Position = 0;
        Assert.Equal(payload, await FrameCodec.TryReadAsync(ms));
    }

    [Fact]
    public async Task Incompressible_Payload_Falls_Back_To_Uncompressed()
    {
        // Trying and losing must not cost size — random bytes inflate under Brotli.
        var payload = Incompressible(100_000);
        using var ms = new MemoryStream();

        await FrameCodec.WriteAsync(ms, payload, compress: true);

        Assert.False(FirstChunkIsCompressed(ms.ToArray()));

        ms.Position = 0;
        Assert.Equal(payload, await FrameCodec.TryReadAsync(ms));
    }

    // ---------------------------------------------------------------- adversarial

    [Fact]
    public async Task Compression_Bomb_Is_Refused_Not_Allocated()
    {
        // 64 MiB of zeroes compresses to a couple of KiB. A reader that inflated first and
        // checked afterwards would allocate all 64 MiB on behalf of an unauthenticated peer.
        var bomb = new byte[64 * 1024 * 1024];
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(bomb, 0, bomb.Length);

        var compressedBytes = compressed.ToArray();
        Assert.True(compressedBytes.Length < 256 * 1024, "the bomb must actually be small on the wire");

        using var ms = new MemoryStream();
        WriteRawFrame(ms, compressedBytes, compressedFlag: true);
        ms.Position = 0;

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            FrameCodec.TryReadAsync(ms, maxChunkPayload: 1024 * 1024, maxMessageSize: 1024 * 1024));
        Assert.Contains("Decompressed message would exceed", ex.Message);
    }

    [Fact]
    public async Task Compressed_Flag_Flipping_MidFrame_Is_Rejected()
    {
        // The flag describes the whole frame. A peer that changes it between chunks is
        // malformed, and silently honouring the first value would be a parser ambiguity.
        using var ms = new MemoryStream();
        WriteChunk(ms, new byte[16], isFinal: false, compressed: true);
        WriteChunk(ms, new byte[16], isFinal: true, compressed: false);
        ms.Position = 0;

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.TryReadAsync(ms));
        Assert.Contains("compression flag changed", ex.Message);
    }

    [Fact]
    public async Task Garbage_Marked_As_Compressed_Is_A_ProtocolException()
    {
        // Not an unhandled InvalidDataException escaping the codec.
        using var ms = new MemoryStream();
        WriteRawFrame(ms, Encoding.ASCII.GetBytes("this is definitely not brotli"), compressedFlag: true);
        ms.Position = 0;

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.TryReadAsync(ms));
        Assert.Contains("Brotli", ex.Message);
    }

    // ---------------------------------------------------------------- raw framing helpers

    private static void WriteRawFrame(Stream stream, byte[] payload, bool compressedFlag)
        => WriteChunk(stream, payload, isFinal: true, compressed: compressedFlag);

    private static void WriteChunk(Stream stream, byte[] payload, bool isFinal, bool compressed)
    {
        var header = new byte[FrameCodec.ChunkHeaderSize];
        Encoding.ASCII.GetBytes(ProtocolVersion.Magic).CopyTo(header, 0);
        byte flags = 0;
        if (isFinal) flags |= 0x01;
        if (compressed) flags |= 0x02;
        header[4] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), (uint)payload.Length);
        stream.Write(header, 0, header.Length);
        stream.Write(payload, 0, payload.Length);
    }
}
