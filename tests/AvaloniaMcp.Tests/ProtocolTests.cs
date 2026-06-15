using System.IO;
using System.Text;
using System.Text.Json;
using AvaloniaMcp.Protocol;
using Xunit;

namespace AvaloniaMcp.Tests;

/// <summary>
/// Unit tests for the dependency-free <see cref="AvaloniaMcp.Protocol"/> substrate:
/// the chunked length-prefixed <see cref="FrameCodec"/> and the message envelope
/// round-trip. These need no UI thread — they exercise pure data + framing.
/// </summary>
public class ProtocolTests
{
    [Fact]
    public async Task FrameCodec_RoundTrips_Small_Payload()
    {
        var payload = Encoding.UTF8.GetBytes("hello broker");
        using var ms = new MemoryStream();

        FrameCodec.Write(ms, payload);
        ms.Position = 0;

        var read = await FrameCodec.TryReadAsync(ms);
        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task FrameCodec_RoundTrips_Empty_Payload()
    {
        using var ms = new MemoryStream();
        FrameCodec.Write(ms, ReadOnlySpan<byte>.Empty);
        ms.Position = 0;

        var read = await FrameCodec.TryReadAsync(ms);
        Assert.NotNull(read);
        Assert.Empty(read!);
    }

    [Fact]
    public async Task FrameCodec_Chunks_And_Reassembles_Large_Payload()
    {
        // A payload several times the chunk size forces multi-chunk framing,
        // mirroring a base64 PNG screenshot crossing the wire.
        var payload = new byte[FrameCodec.DefaultMaxChunkPayload * 3 + 123];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i * 31 + 7);

        using var ms = new MemoryStream();
        FrameCodec.Write(ms, payload);

        // The framed bytes must be larger than the payload (chunk headers added),
        // proving it actually chunked rather than wrote one blob.
        Assert.True(ms.Length > payload.Length + FrameCodec.ChunkHeaderSize);

        ms.Position = 0;
        var read = await FrameCodec.TryReadAsync(ms);
        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task FrameCodec_Reads_Multiple_Frames_In_Sequence()
    {
        var a = Encoding.UTF8.GetBytes("frame-one");
        var b = Encoding.UTF8.GetBytes("frame-two-is-longer");

        using var ms = new MemoryStream();
        FrameCodec.Write(ms, a);
        FrameCodec.Write(ms, b);
        ms.Position = 0;

        Assert.Equal(a, await FrameCodec.TryReadAsync(ms));
        Assert.Equal(b, await FrameCodec.TryReadAsync(ms));
        Assert.Null(await FrameCodec.TryReadAsync(ms)); // clean EOF between frames
    }

    [Fact]
    public async Task FrameCodec_Truncated_Frame_Throws_ProtocolException()
    {
        var payload = Encoding.UTF8.GetBytes("incomplete");
        using var full = new MemoryStream();
        FrameCodec.Write(full, payload);

        // Drop the last few bytes to truncate the chunk payload mid-stream.
        var bytes = full.ToArray();
        using var truncated = new MemoryStream(bytes, 0, bytes.Length - 3);

        await Assert.ThrowsAsync<ProtocolException>(
            async () => await FrameCodec.TryReadAsync(truncated));
    }

    [Fact]
    public void ProtocolVersion_Compatibility_Honors_Range()
    {
        Assert.True(ProtocolVersion.IsCompatible(ProtocolVersion.Current));
        Assert.True(ProtocolVersion.IsCompatible(ProtocolVersion.Minimum));
        Assert.False(ProtocolVersion.IsCompatible(ProtocolVersion.Current + 1));
        Assert.False(ProtocolVersion.IsCompatible(ProtocolVersion.Minimum - 1));
    }

    [Fact]
    public async Task MessageEnvelope_Wraps_And_Unwraps_InvokeTool_Through_FrameCodec()
    {
        var invoke = new InvokeToolMessage
        {
            ClientId = "client-1",
            ToolName = "screenshot_window",
            Arguments = JsonSerializer.SerializeToElement(new { target = "Window[Name=main]" }),
        };
        var envelope = MessageEnvelope.Wrap(MessageKind.InvokeTool, invoke, correlationId: "corr-42");

        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);

        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, json);
        ms.Position = 0;

        var framed = await FrameCodec.TryReadAsync(ms);
        Assert.NotNull(framed);

        var back = JsonSerializer.Deserialize<MessageEnvelope>(framed!, ProtocolJson.Options);
        Assert.NotNull(back);
        Assert.Equal(MessageKind.InvokeTool, back!.Kind);
        Assert.Equal("corr-42", back.CorrelationId);
        Assert.Equal(ProtocolVersion.Current, back.Version);

        var payload = back.Unwrap<InvokeToolMessage>();
        Assert.NotNull(payload);
        Assert.Equal("client-1", payload!.ClientId);
        Assert.Equal("screenshot_window", payload.ToolName);
    }
}
