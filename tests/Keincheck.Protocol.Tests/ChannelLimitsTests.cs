using System.Text;
using System.Text.Json;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// Tests for per-channel framing bounds. These exist because a network listener accepts
/// bytes from a peer it has not yet identified: without a pre-auth clamp, anyone who can
/// complete a TCP connect could make the hub reassemble up to 32 MiB per connection.
/// </summary>
public class ChannelLimitsTests
{
    /// <summary>A one-way stream pair so a channel can be written on one end and read on the other.</summary>
    private static (PipeChannel Writer, PipeChannel Reader, MemoryStream Backing) Pair(
        ChannelLimits? writerLimits = null, ChannelLimits? readerLimits = null)
    {
        var backing = new MemoryStream();
        var writer = new PipeChannel(new NonClosingStream(backing), ownsStream: false, writerLimits);
        var reader = new PipeChannel(new NonClosingStream(backing), ownsStream: false, readerLimits);
        return (writer, reader, backing);
    }

    [Fact]
    public void Default_Limits_Match_FrameCodec_Defaults()
    {
        Assert.Equal(FrameCodec.DefaultMaxChunkPayload, ChannelLimits.Default.MaxChunkPayload);
        Assert.Equal(FrameCodec.DefaultMaxMessageSize, ChannelLimits.Default.MaxMessageSize);
        Assert.False(ChannelLimits.Default.AllowCompression);
    }

    [Fact]
    public void Channel_Without_Explicit_Limits_Uses_Default()
    {
        using var channel = new PipeChannel(new MemoryStream());
        Assert.Same(ChannelLimits.Default, channel.Limits);
    }

    [Fact]
    public void Handshake_Limits_Are_Far_Smaller_Than_Default()
    {
        // The exact numbers are policy; that they are orders of magnitude smaller is the point.
        Assert.True(ChannelLimits.Handshake.MaxMessageSize < ChannelLimits.Default.MaxMessageSize / 100);
        Assert.False(ChannelLimits.Handshake.AllowCompression);
    }

    [Fact]
    public async Task Handshake_Limits_Refuse_An_Oversized_PreAuth_Frame()
    {
        // The attack: connect, then send a huge frame before authenticating.
        var (writer, reader, backing) = Pair(
            writerLimits: ChannelLimits.Default,
            readerLimits: ChannelLimits.Handshake);

        using (writer)
        using (reader)
        {
            var big = new string('x', ChannelLimits.Handshake.MaxMessageSize * 4);
            await writer.SendAsync(MessageKind.Hello, new HelloMessage { MachineName = big });

            backing.Position = 0;
            var ex = await Assert.ThrowsAsync<ProtocolException>(() => reader.ReceiveAsync());
            Assert.Contains("exceeds the maximum", ex.Message);
        }
    }

    [Fact]
    public async Task Widening_Limits_After_Auth_Lets_A_Large_Payload_Through()
    {
        // Same channel object, same peer -- only the trust level changed. This is exactly
        // what the remote listener does the moment it sends Welcome.
        var (writer, reader, backing) = Pair(readerLimits: ChannelLimits.Handshake);

        using (writer)
        using (reader)
        {
            var payload = new string('y', 512 * 1024);
            await writer.SendAsync(MessageKind.ToolResult, new ToolResultMessage
            {
                ClientId = "app",
                ToolName = "screenshot_window",
                Content = JsonSerializer.SerializeToElement(payload),
            });

            backing.Position = 0;
            await Assert.ThrowsAsync<ProtocolException>(() => reader.ReceiveAsync());

            // Now accept the session and retry the identical frame.
            reader.Limits = ChannelLimits.Default;
            backing.Position = 0;
            var envelope = await reader.ReceiveAsync();

            Assert.NotNull(envelope);
            Assert.Equal(MessageKind.ToolResult, envelope!.Kind);
            Assert.Equal(payload, envelope.Unwrap<ToolResultMessage>()!.Content!.Value.GetString());
        }
    }

    [Fact]
    public async Task AllowCompression_Is_What_Actually_Emits_A_Compressed_Frame()
    {
        var compressible = new string('z', 200_000);

        // Off by default, even for a very compressible payload.
        var plain = await FrameFor(ChannelLimits.Default, compressible);
        Assert.False((plain[4] & 0x02) != 0);

        // On once negotiated.
        var compressed = await FrameFor(ChannelLimits.Default with { AllowCompression = true }, compressible);
        Assert.True((compressed[4] & 0x02) != 0);
        Assert.True(compressed.Length < plain.Length / 2);

        // And a reader with compression OFF still reads it -- only writing is gated, so a
        // capability mismatch can never strand a frame.
        using var backing = new MemoryStream(compressed);
        using var reader = new PipeChannel(backing, ownsStream: false, ChannelLimits.Default);
        var envelope = await reader.ReceiveAsync();
        Assert.Equal(compressible, envelope!.Unwrap<HelloMessage>()!.MachineName);

        static async Task<byte[]> FrameFor(ChannelLimits limits, string text)
        {
            using var ms = new MemoryStream();
            using var channel = new PipeChannel(ms, ownsStream: false, limits);
            await channel.SendAsync(MessageKind.Hello, new HelloMessage { MachineName = text });
            return ms.ToArray();
        }
    }

    [Fact]
    public void Limits_Cannot_Be_Set_To_Null()
    {
        using var channel = new PipeChannel(new MemoryStream());
        Assert.Throws<ArgumentNullException>(() => channel.Limits = null!);
    }

    /// <summary>Keeps the shared backing store alive when one wrapper is disposed.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override long Seek(long off, SeekOrigin origin) => inner.Seek(off, origin);
        public override void SetLength(long v) => inner.SetLength(v);
        public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
    }
}
