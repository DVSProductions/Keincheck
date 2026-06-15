using System.Buffers.Binary;
using System.Text;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// Exhaustive tests for the chunked, length-prefixed <see cref="FrameCodec"/>:
/// byte-identical round-trips, multi-megabyte chunking + reassembly, partial /
/// streamed reads, framing-violation detection, and the over-cap guards. These
/// are the wire-level guarantees the broker depends on to move PNG screenshots.
/// </summary>
public class FrameCodecTests
{
    private static byte[] Patterned(int length, int seed = 1)
    {
        // A deterministic, non-trivial pattern so a single flipped/lost byte is caught.
        var data = new byte[length];
        var x = (uint)(seed * 2654435761u + 1u);
        for (var i = 0; i < length; i++)
        {
            x = x * 1103515245u + 12345u;
            data[i] = (byte)(x >> 16);
        }
        return data;
    }

    // ---------------------------------------------------------------- round-trip

    [Fact]
    public async Task Sync_Write_RoundTrips_Identical_Small_Payload()
    {
        var payload = Encoding.UTF8.GetBytes("hello broker éñ☃");
        using var ms = new MemoryStream();

        FrameCodec.Write(ms, payload);
        ms.Position = 0;

        var read = await FrameCodec.TryReadAsync(ms);
        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Async_Write_RoundTrips_Identical_Payload()
    {
        var payload = Patterned(40_000);
        using var ms = new MemoryStream();

        await FrameCodec.WriteAsync(ms, payload);
        ms.Position = 0;

        var read = await FrameCodec.TryReadAsync(ms);
        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Empty_Payload_RoundTrips_To_Empty()
    {
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, ReadOnlySpan<byte>.Empty);
        ms.Position = 0;

        // Exactly one final chunk with N == 0: header only, no payload bytes.
        Assert.Equal(FrameCodec.ChunkHeaderSize, ms.Length);

        var read = await FrameCodec.TryReadAsync(ms);
        Assert.NotNull(read);
        Assert.Empty(read!);
    }

    [Fact]
    public async Task Sync_And_Async_Writers_Produce_IdenticalBytes()
    {
        var payload = Patterned(FrameCodec.DefaultMaxChunkPayload * 2 + 17, seed: 9);

        using var sync = new MemoryStream();
        FrameCodec.Write(sync, payload);

        using var async = new MemoryStream();
        await FrameCodec.WriteAsync(async, payload);

        Assert.Equal(sync.ToArray(), async.ToArray());
    }

    // ---------------------------------------------------------------- chunking

    [Fact]
    public async Task Chunks_And_Reassembles_MultiMegabyte_Payload()
    {
        // ~5 MiB: many default-sized chunks, mirroring a large base64 PNG.
        var payload = Patterned(5 * 1024 * 1024 + 777, seed: 5);
        using var ms = new MemoryStream();

        await FrameCodec.WriteAsync(ms, payload);

        // Prove it actually chunked: framed length carries one header per chunk.
        var expectedChunks = (payload.Length + FrameCodec.DefaultMaxChunkPayload - 1)
                             / FrameCodec.DefaultMaxChunkPayload;
        Assert.True(expectedChunks > 1);
        Assert.Equal(
            payload.Length + (long)expectedChunks * FrameCodec.ChunkHeaderSize,
            ms.Length);

        ms.Position = 0;
        // The message cap must comfortably admit a multi-MiB screenshot.
        var read = await FrameCodec.TryReadAsync(ms);
        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Payload_ExactlyOneChunk_EmitsSingleChunk()
    {
        var payload = Patterned(FrameCodec.DefaultMaxChunkPayload);
        using var ms = new MemoryStream();

        FrameCodec.Write(ms, payload);

        // Exactly one full chunk: a single header + payload, no trailing empty chunk.
        Assert.Equal(payload.Length + FrameCodec.ChunkHeaderSize, ms.Length);

        ms.Position = 0;
        Assert.Equal(payload, await FrameCodec.TryReadAsync(ms));
    }

    [Fact]
    public async Task Payload_OneByteOverChunk_EmitsTwoChunks()
    {
        var payload = Patterned(FrameCodec.DefaultMaxChunkPayload + 1);
        using var ms = new MemoryStream();

        FrameCodec.Write(ms, payload);

        Assert.Equal(payload.Length + 2 * FrameCodec.ChunkHeaderSize, ms.Length);

        ms.Position = 0;
        Assert.Equal(payload, await FrameCodec.TryReadAsync(ms));
    }

    [Fact]
    public async Task CustomChunkSize_RoundTrips_And_Splits()
    {
        var payload = Patterned(1000);
        const int chunk = 100;
        using var ms = new MemoryStream();

        FrameCodec.Write(ms, payload, maxChunkPayload: chunk);

        // 10 chunks * (header + 100) bytes.
        Assert.Equal((long)10 * (FrameCodec.ChunkHeaderSize + chunk), ms.Length);

        ms.Position = 0;
        var read = await FrameCodec.TryReadAsync(ms, maxChunkPayload: chunk);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task SingleByte_Payload_RoundTrips()
    {
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, new byte[] { 0xAB });
        ms.Position = 0;

        var read = await FrameCodec.TryReadAsync(ms);
        Assert.Equal(new byte[] { 0xAB }, read);
    }

    // ---------------------------------------------------------------- streaming

    [Fact]
    public async Task PartialStreamedReads_Reassemble_FullMessage()
    {
        // A drip-feed stream that yields ONE byte per ReadAsync forces the codec's
        // ReadAtLeast loop to splice many partial reads back into whole chunks.
        var payload = Patterned(FrameCodec.DefaultMaxChunkPayload + 4096, seed: 3);
        byte[] framed;
        using (var buf = new MemoryStream())
        {
            await FrameCodec.WriteAsync(buf, payload);
            framed = buf.ToArray();
        }

        using var drip = new DripStream(framed, maxPerRead: 1);
        var read = await FrameCodec.TryReadAsync(drip);

        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task SmallChunkedReads_AcrossChunkBoundaries_Reassemble()
    {
        var payload = Patterned(300_000, seed: 11);
        byte[] framed;
        using (var buf = new MemoryStream())
        {
            await FrameCodec.WriteAsync(buf, payload);
            framed = buf.ToArray();
        }

        // 7 bytes per read deliberately straddles the 9-byte header boundary.
        using var drip = new DripStream(framed, maxPerRead: 7);
        var read = await FrameCodec.TryReadAsync(drip);

        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Reads_Multiple_Frames_In_Sequence_Then_CleanEof()
    {
        var a = Encoding.UTF8.GetBytes("frame-one");
        var b = Patterned(FrameCodec.DefaultMaxChunkPayload * 2 + 5, seed: 2);
        var c = Array.Empty<byte>();

        using var ms = new MemoryStream();
        FrameCodec.Write(ms, a);
        FrameCodec.Write(ms, b);
        FrameCodec.Write(ms, c);
        ms.Position = 0;

        Assert.Equal(a, await FrameCodec.TryReadAsync(ms));
        Assert.Equal(b, await FrameCodec.TryReadAsync(ms));
        Assert.Equal(c, await FrameCodec.TryReadAsync(ms));
        Assert.Null(await FrameCodec.TryReadAsync(ms)); // clean EOF between frames
    }

    [Fact]
    public async Task EmptyStream_ReadsAsNull()
    {
        using var ms = new MemoryStream();
        Assert.Null(await FrameCodec.TryReadAsync(ms));
    }

    // ---------------------------------------------------------------- violations

    [Fact]
    public async Task Truncated_Payload_Throws_ProtocolException()
    {
        var payload = Encoding.UTF8.GetBytes("incomplete-on-the-wire");
        byte[] framed;
        using (var full = new MemoryStream())
        {
            FrameCodec.Write(full, payload);
            framed = full.ToArray();
        }

        using var truncated = new MemoryStream(framed, 0, framed.Length - 3);
        await Assert.ThrowsAsync<ProtocolException>(
            () => FrameCodec.TryReadAsync(truncated));
    }

    [Fact]
    public async Task Truncated_Header_Throws_ProtocolException()
    {
        // Fewer bytes than a full 9-byte chunk header => mid-header EOF.
        var framed = new byte[] { (byte)'A', (byte)'M', (byte)'C', (byte)'P', 0x01 };
        using var ms = new MemoryStream(framed);

        await Assert.ThrowsAsync<ProtocolException>(
            () => FrameCodec.TryReadAsync(ms));
    }

    [Fact]
    public async Task Magic_Mismatch_Throws_ProtocolException()
    {
        // Valid-length header but the wrong magic token => stream not aligned.
        var header = new byte[FrameCodec.ChunkHeaderSize];
        Encoding.ASCII.GetBytes("XXXX").CopyTo(header, 0);
        header[4] = 0x01; // final
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), 0);

        using var ms = new MemoryStream(header);
        await Assert.ThrowsAsync<ProtocolException>(
            () => FrameCodec.TryReadAsync(ms));
    }

    [Fact]
    public async Task Chunk_Larger_Than_MaxChunkPayload_Throws()
    {
        // Writer uses a bigger chunk than the reader will admit.
        var payload = Patterned(5000);
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, payload, maxChunkPayload: 5000);
        ms.Position = 0;

        await Assert.ThrowsAsync<ProtocolException>(
            () => FrameCodec.TryReadAsync(ms, maxChunkPayload: 1000));
    }

    [Fact]
    public async Task Reassembled_Message_Over_MaxMessageSize_Throws()
    {
        // Several small chunks whose SUM exceeds the per-message cap.
        const int chunk = 256;
        var payload = Patterned(chunk * 10);
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, payload, maxChunkPayload: chunk);
        ms.Position = 0;

        await Assert.ThrowsAsync<ProtocolException>(
            () => FrameCodec.TryReadAsync(ms, maxChunkPayload: chunk, maxMessageSize: chunk * 3));
    }

    [Fact]
    public async Task MessageSizeCap_AtExactLimit_Succeeds()
    {
        // A message exactly at the cap must be accepted (boundary is inclusive).
        var payload = Patterned(1024);
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, payload, maxChunkPayload: 256);
        ms.Position = 0;

        var read = await FrameCodec.TryReadAsync(ms, maxChunkPayload: 256, maxMessageSize: 1024);
        Assert.Equal(payload, read);
    }

    // ---------------------------------------------------------------- arg guards

    [Fact]
    public void Write_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FrameCodec.Write(null!, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Write_NonPositiveChunkSize_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameCodec.Write(ms, new byte[] { 1 }, maxChunkPayload: 0));
    }

    [Fact]
    public async Task WriteAsync_NullStream_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => FrameCodec.WriteAsync(null!, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task TryReadAsync_NullStream_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => FrameCodec.TryReadAsync(null!));
    }

    // ---------------------------------------------------------------- wire shape

    [Fact]
    public void WrittenHeader_HasExpected_Magic_FinalFlag_And_BigEndianLength()
    {
        var payload = Patterned(50);
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, payload);

        var bytes = ms.ToArray();
        Assert.Equal((byte)'A', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);
        Assert.Equal((byte)'C', bytes[2]);
        Assert.Equal((byte)'P', bytes[3]);
        Assert.Equal(0x01, bytes[4] & 0x01); // single chunk => final bit set
        Assert.Equal((uint)50, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4)));
    }

    [Fact]
    public void MultiChunk_NonFinalChunks_DoNotSetFinalBit()
    {
        var payload = Patterned(250);
        const int chunk = 100;
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, payload, maxChunkPayload: chunk);

        var bytes = ms.ToArray();
        // chunk 0 header flags at offset 4; chunk 1 at 4 + (header+chunk); chunk 2 likewise.
        var stride = FrameCodec.ChunkHeaderSize + chunk;
        Assert.Equal(0x00, bytes[4] & 0x01);                 // chunk 0: not final
        Assert.Equal(0x00, bytes[4 + stride] & 0x01);        // chunk 1: not final
        Assert.Equal(0x01, bytes[4 + 2 * stride] & 0x01);    // chunk 2 (50 bytes): final
    }

    /// <summary>
    /// A stream that hands back at most <paramref name="maxPerRead"/> bytes per
    /// <c>ReadAsync</c>, simulating a real network/pipe transport that delivers a
    /// frame in many partial reads. Exercises the codec's read-loop reassembly.
    /// </summary>
    private sealed class DripStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxPerRead;
        private int _pos;

        public DripStream(byte[] data, int maxPerRead)
        {
            _data = data;
            _maxPerRead = Math.Max(1, maxPerRead);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length) return 0;
            var n = Math.Min(Math.Min(_maxPerRead, count), _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= _data.Length) return ValueTask.FromResult(0);
            var n = Math.Min(Math.Min(_maxPerRead, buffer.Length), _data.Length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
