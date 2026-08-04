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

        await FrameCodec.WriteAsync(ms, payload, FrameCodec.DefaultMaxChunkPayload, compress: true);
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
        FrameCodec.Write(sync, payload, FrameCodec.DefaultMaxChunkPayload, compress: true);

        using var async = new MemoryStream();
        await FrameCodec.WriteAsync(async, payload, FrameCodec.DefaultMaxChunkPayload, compress: true);

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

        await FrameCodec.WriteAsync(ms, payload, FrameCodec.DefaultMaxChunkPayload, compress: true);

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

        await FrameCodec.WriteAsync(ms, payload, FrameCodec.DefaultMaxChunkPayload, compress: true);

        Assert.False(FirstChunkIsCompressed(ms.ToArray()));

        ms.Position = 0;
        Assert.Equal(payload, await FrameCodec.TryReadAsync(ms));
    }

    // ---------------------------------------------------------------- adversarial

    /// <summary>A frame carrying <paramref name="size"/> bytes of zeroes, compressed.</summary>
    private static byte[] BombFrame(int size)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(new byte[size], 0, size);

        var payload = compressed.ToArray();
        Assert.True(payload.Length < 256 * 1024, "the bomb must actually be small on the wire");

        using var framed = new MemoryStream();
        WriteRawFrame(framed, payload, compressedFlag: true);
        return framed.ToArray();
    }

    [Theory]
    [InlineData(1024 * 1024)]
    [InlineData(4 * 1024 * 1024)]
    public async Task A_Compression_Bomb_Is_Refused_Against_The_CALLER_S_Cap(int cap)
    {
        // 64 MiB of zeroes compresses to a couple of KiB.
        //
        // Parameterised over two caps, and asserting the cap's VALUE, because the previous
        // version of this test only matched the message prefix. That let the guard use any
        // constant it liked: swapping the caller's maxMessageSize for
        // FrameCodec.DefaultMaxMessageSize still threw (64 MiB > 32 MiB), still matched, and
        // still passed -- while an unauthenticated peer bounded to ChannelLimits.Handshake's
        // 16 KiB could make the hub inflate 32 MiB.
        using var ms = new MemoryStream(BombFrame(64 * 1024 * 1024));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            FrameCodec.TryReadAsync(ms, maxChunkPayload: 1024 * 1024, maxMessageSize: cap));

        Assert.Contains($"exceed the maximum of {cap} bytes", ex.Message);
    }

    [Fact]
    public void A_Compression_Bomb_Is_Refused_WITHOUT_Inflating_It_First()
    {
        // The other half of the name, which was previously unasserted entirely. Rewriting
        // Decompress as "inflate fully, then compare" keeps the same exception and the same
        // message, so no assertion on the throw can distinguish it -- yet it removes the bound
        // completely: the allocation becomes the full decompressed size, limited only by
        // MemoryStream's ~2 GiB ceiling. Measuring allocation is the only thing that tells
        // "refused" apart from "refused, after doing the damage".
        const int Cap = 1024 * 1024;
        var frame = BombFrame(64 * 1024 * 1024);

        // Warm up so first-call JIT and BrotliStream's own setup are not counted.
        try { Read(BombFrame(2 * 1024 * 1024)); } catch (ProtocolException) { }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ProtocolException>(() => Read(frame));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A bounded reader allocates roughly the cap plus its read buffer. An inflate-first
        // reader allocates the full 64 MiB (and more, as the MemoryStream doubles). 16 MiB
        // sits far from both.
        Assert.True(allocated < 16 * 1024 * 1024,
            $"decompression allocated {allocated / (1024 * 1024)} MiB for a {Cap / (1024 * 1024)} MiB cap; " +
            "the bound is being applied after inflating rather than as output is produced.");

        static void Read(byte[] framed)
        {
            using var ms = new MemoryStream(framed);
            // Synchronous on purpose: allocation is measured per-thread, and a MemoryStream
            // read completes inline so nothing hops to the pool.
#pragma warning disable xUnit1031
            FrameCodec.TryReadAsync(ms, maxChunkPayload: 1024 * 1024, maxMessageSize: Cap)
                .GetAwaiter().GetResult();
#pragma warning restore xUnit1031
        }
    }

    [Fact]
    public async Task A_Compressed_Frame_Is_Bounded_By_The_Pre_Auth_Handshake_Clamp()
    {
        // The composition that exists in production and appeared in no test: a channel still
        // on ChannelLimits.Handshake (StreamTransport builds one for every accepted socket)
        // receiving a compressed frame. Reading a compressed frame is always supported even
        // when writing them is not, so a peer that has done nothing but complete TCP and TLS
        // can reach the decompressor -- against a 16 KiB budget, not the 32 MiB default.
        using var backing = new MemoryStream(BombFrame(64 * 1024 * 1024));
        using var channel = new PipeChannel(backing, ownsStream: false, ChannelLimits.Handshake);

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => channel.ReceiveAsync());

        // Whatever it complains about, the figure must be the handshake budget -- never the
        // default. Either guard may fire first; both must use the channel's own limits.
        Assert.Contains(
            ChannelLimits.Handshake.MaxMessageSize.ToString(),
            ex.Message + ChannelLimits.Handshake.MaxChunkPayload);
        Assert.DoesNotContain(FrameCodec.DefaultMaxMessageSize.ToString(), ex.Message);
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
