using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// The two ways a peer could previously end a session it should only have been ignored by:
/// an endlessly-padded frame, and a message kind from a newer build.
/// </summary>
public class ForwardCompatibilityTests
{
    /// <summary>How long a bounded read is given before the test calls it a spin.</summary>
    private static readonly TimeSpan SpinBudget = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------------ empty-chunk padding

    /// <summary>
    /// An infinite source of well-formed, empty, non-final chunks.
    /// </summary>
    /// <remarks>
    /// A <see cref="MemoryStream"/> cannot express this bug: it runs out, and the read fails with
    /// "stream ended in the middle of a chunk header" — a pass for the wrong reason. The defect is
    /// only observable against a stream that never ends, which is exactly what a socket is.
    /// </remarks>
    private sealed class EndlessEmptyChunks : Stream
    {
        private readonly byte[] _chunk;
        private int _offset;

        public EndlessEmptyChunks()
        {
            _chunk = new byte[FrameCodec.ChunkHeaderSize];
            Encoding.ASCII.GetBytes(ProtocolVersion.Magic).CopyTo(_chunk, 0);
            _chunk[4] = 0;  // not final, not compressed
            BinaryPrimitives.WriteUInt32BigEndian(_chunk.AsSpan(5, 4), 0u);  // and no payload
        }

        /// <summary>Bytes handed out so far — the budget a spinning reader burns through.</summary>
        public long BytesServed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = 0;
            while (n < count)
            {
                buffer[offset + n++] = _chunk[_offset];
                _offset = (_offset + 1) % _chunk.Length;
            }
            BytesServed += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Endless_Empty_NonFinal_Chunks_Are_Refused_Rather_Than_Spun_On()
    {
        // The one input both framing caps are blind to. `chunkLength == 0` is not greater than
        // maxChunkPayload, and it adds nothing to the reassembly buffer, so neither bound can
        // ever fire however long the peer keeps going. Before the guard this read never returned:
        // no exception, no EOF, no allocation — just a session task parked forever holding its
        // socket. Bounded caps are not the same as a bounded loop.
        using var stream = new EndlessEmptyChunks();

        var read = Task.Run(() => FrameCodec.TryReadAsync(
            stream, maxChunkPayload: 64 * 1024, maxMessageSize: 1024 * 1024));

        // Assert on a race rather than awaiting directly: a regression here does not fail, it
        // hangs, and a hung test takes the whole suite with it.
        var winner = await Task.WhenAny(read, Task.Delay(SpinBudget));
        Assert.True(ReferenceEquals(winner, read),
            $"TryReadAsync was still running after {SpinBudget.TotalSeconds:0}s having consumed " +
            $"{stream.BytesServed:N0} bytes; the empty-chunk guard is gone and the loop is unbounded.");

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => read);
        Assert.Contains("Empty non-final chunk", ex.Message);
    }

    [Fact]
    public async Task An_Empty_Message_Still_RoundTrips()
    {
        // The guard must reject padding without rejecting the legitimate empty payload, which the
        // wire format defines as a single FINAL chunk of length zero.
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, ReadOnlySpan<byte>.Empty);
        Assert.Equal(FrameCodec.ChunkHeaderSize, ms.Length);

        ms.Position = 0;
        var read = await FrameCodec.TryReadAsync(ms);
        Assert.NotNull(read);
        Assert.Empty(read!);
    }

    [Fact]
    public async Task A_MultiChunk_Frame_With_Real_Payloads_Still_RoundTrips()
    {
        // Non-final chunks are still legal — only data-less ones are not.
        var payload = Encoding.UTF8.GetBytes(new string('x', 40_000));
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, payload, maxChunkPayload: 4096);

        ms.Position = 0;
        Assert.Equal(payload, await FrameCodec.TryReadAsync(ms, maxChunkPayload: 4096));
    }

    // ------------------------------------------------------------------ unknown message kinds

    private static async Task<MemoryStream> FramedJsonAsync(string json)
    {
        var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, Encoding.UTF8.GetBytes(json));
        ms.Position = 0;
        return ms;
    }

    [Theory]
    [InlineData("\"SomeKindFromV3\"")]   // a name added by a newer build
    [InlineData("\"\"")]                 // an empty name
    [InlineData("99")]                   // the numeric form, out of range
    [InlineData("null")]
    public async Task A_Kind_This_Build_Does_Not_Know_Reads_As_Unknown(string kindToken)
    {
        // ProtocolVersion promises growth is additive and that an older peer ignores what it does
        // not recognise. JsonStringEnumConverter broke that at the first hurdle: an unrecognised
        // name threw JsonException out of ReceiveAsync, and every production receive loop treats
        // any exception as fatal-for-the-connection. So the dispatch switch — which has no default
        // case precisely so unknown kinds fall through harmlessly — was never reached.
        using var ms = await FramedJsonAsync($"{{\"kind\":{kindToken},\"protocolVersion\":99}}");
        using var channel = new PipeChannel(ms, ownsStream: false);

        var envelope = await channel.ReceiveAsync();

        Assert.NotNull(envelope);
        Assert.Equal(MessageKind.Unknown, envelope!.Kind);
    }

    [Fact]
    public async Task An_Unknown_Kind_Does_Not_Consume_The_Frames_After_It()
    {
        // The property that actually matters is not "it parses" but "the session survives it":
        // a v3 hub interleaving a new kind must leave the v2 peer able to read the next frame.
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, Encoding.UTF8.GetBytes("{\"kind\":\"FromTheFuture\"}"));
        await FrameCodec.WriteAsync(ms, JsonSerializer.SerializeToUtf8Bytes(
            MessageEnvelope.Wrap(MessageKind.Heartbeat, new HeartbeatMessage()), ProtocolJson.Options));
        ms.Position = 0;

        using var channel = new PipeChannel(ms, ownsStream: false);

        Assert.Equal(MessageKind.Unknown, (await channel.ReceiveAsync())!.Kind);
        Assert.Equal(MessageKind.Heartbeat, (await channel.ReceiveAsync())!.Kind);
        Assert.Null(await channel.ReceiveAsync());
    }

    [Fact]
    public async Task Known_Kinds_Still_Parse_And_Are_Written_As_Names()
    {
        // The tolerant reader must not become a tolerant *writer*: kinds stay stable strings on
        // the wire, or a v1 peer stops recognising them.
        var json = JsonSerializer.Serialize(
            MessageEnvelope.Wrap(MessageKind.ToolResult, new ToolResultMessage()), ProtocolJson.Options);
        Assert.Contains("\"ToolResult\"", json);

        using var ms = await FramedJsonAsync(json);
        using var channel = new PipeChannel(ms, ownsStream: false);
        Assert.Equal(MessageKind.ToolResult, (await channel.ReceiveAsync())!.Kind);
    }

    [Fact]
    public async Task A_Structurally_Wrong_Kind_Is_Still_An_Error()
    {
        // Tolerance is for names this build has not heard of, not for malformed JSON. An object
        // where a discriminator belongs is a broken peer, and should not be silently downgraded
        // to "some future kind" — that would hide real corruption behind the compatibility rule.
        using var ms = await FramedJsonAsync("{\"kind\":{\"nested\":true}}");
        using var channel = new PipeChannel(ms, ownsStream: false);

        await Assert.ThrowsAnyAsync<Exception>(() => channel.ReceiveAsync());
    }
}
