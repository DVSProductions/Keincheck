using System.Text.Json;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// The full broker send/receive pipeline end to end:
/// <c>Wrap → serialize(ProtocolJson) → FrameCodec.Write → FrameCodec.TryRead →
/// deserialize → switch(Kind) → Unwrap</c>. This is exactly how the Stage-2
/// Client/Hub will move messages, so it must hold for small control messages and
/// for a multi-megabyte screenshot result alike.
/// </summary>
public class EndToEndPipelineTests
{
    private static async Task<MessageEnvelope> SendAndReceive(MessageEnvelope env, int maxChunkPayload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(env, ProtocolJson.Options);

        using var wire = new MemoryStream();
        await FrameCodec.WriteAsync(wire, json, maxChunkPayload);
        wire.Position = 0;

        var framed = await FrameCodec.TryReadAsync(wire, maxChunkPayload);
        Assert.NotNull(framed);

        var back = JsonSerializer.Deserialize<MessageEnvelope>(framed!, ProtocolJson.Options);
        Assert.NotNull(back);
        return back!;
    }

    [Fact]
    public async Task Register_FlowsThrough_FullPipeline()
    {
        var env = MessageEnvelope.Wrap(MessageKind.Register, new RegisterMessage
        {
            ClientId = "proto-1",
            DisplayName = "ProtoFace",
            ProcessId = 4242,
            ProtocolVersion = ProtocolVersion.Current,
        }, correlationId: "h-1");

        var back = await SendAndReceive(env, FrameCodec.DefaultMaxChunkPayload);

        Assert.Equal(MessageKind.Register, back.Kind);
        Assert.Equal("h-1", back.CorrelationId);
        var msg = back.Unwrap<RegisterMessage>();
        Assert.NotNull(msg);
        Assert.Equal("proto-1", msg!.ClientId);
        Assert.Equal("ProtoFace", msg.DisplayName);
        Assert.Equal(4242, msg.ProcessId);
        Assert.True(ProtocolVersion.IsCompatible(msg.ProtocolVersion));
    }

    [Fact]
    public async Task ToolList_FlowsThrough_FullPipeline()
    {
        var env = MessageEnvelope.Wrap(MessageKind.ToolList, new ToolListMessage
        {
            ClientId = "proto-1",
            Tools = new[]
            {
                new ToolDescriptor
                {
                    Name = "screenshot_window",
                    Description = "Render a window to PNG.",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                },
                new ToolDescriptor { Name = "list_windows", Description = "Enumerate top-levels." },
            },
        });

        var back = await SendAndReceive(env, FrameCodec.DefaultMaxChunkPayload);

        Assert.Equal(MessageKind.ToolList, back.Kind);
        var msg = back.Unwrap<ToolListMessage>();
        Assert.NotNull(msg);
        Assert.Equal(2, msg!.Tools.Count);
        Assert.Equal("screenshot_window", msg.Tools[0].Name);
        Assert.NotNull(msg.Tools[0].InputSchema);
        Assert.Null(msg.Tools[1].InputSchema);
    }

    [Fact]
    public async Task ScreenshotResult_MultiMegabyte_FlowsThrough_Chunked_Pipeline()
    {
        // Simulate a real base64 PNG payload of a few MiB inside a ToolResult, which
        // is the whole reason FrameCodec chunks. It must survive the round trip and
        // force multiple wire chunks.
        var pngBytes = new byte[3 * 1024 * 1024 + 91];
        var x = 0x12345u;
        for (var i = 0; i < pngBytes.Length; i++)
        {
            x = x * 1664525u + 1013904223u;
            pngBytes[i] = (byte)(x >> 24);
        }
        var base64 = Convert.ToBase64String(pngBytes);

        var content = JsonSerializer.SerializeToElement(new[]
        {
            new { type = "image", mimeType = "image/png", data = base64 },
        });
        var env = MessageEnvelope.Wrap(MessageKind.ToolResult, new ToolResultMessage
        {
            ClientId = "proto-1",
            ToolName = "screenshot_window",
            IsError = false,
            Content = content,
        }, correlationId: "shot-7");

        var json = JsonSerializer.SerializeToUtf8Bytes(env, ProtocolJson.Options);
        Assert.True(json.Length > FrameCodec.DefaultMaxChunkPayload,
            "the serialized screenshot envelope must exceed one chunk to exercise chunking");

        var back = await SendAndReceive(env, FrameCodec.DefaultMaxChunkPayload);

        Assert.Equal(MessageKind.ToolResult, back.Kind);
        Assert.Equal("shot-7", back.CorrelationId);
        var result = back.Unwrap<ToolResultMessage>();
        Assert.NotNull(result);
        Assert.False(result!.IsError);
        var dataBack = result.Content!.Value[0].GetProperty("data").GetString();
        Assert.Equal(base64, dataBack);

        // The image bytes survived intact through serialize + chunk + reassemble.
        Assert.Equal(pngBytes, Convert.FromBase64String(dataBack!));
    }

    [Fact]
    public async Task InvokeThenResult_CorrelateById_AcrossPipeline()
    {
        const string corr = "req-99";
        var invokeEnv = MessageEnvelope.Wrap(MessageKind.InvokeTool, new InvokeToolMessage
        {
            ClientId = "proto-1",
            ToolName = "get_property",
            Arguments = JsonSerializer.SerializeToElement(new { handle = "ctl-3", property = "Text" }),
        }, correlationId: corr);

        var resultEnv = MessageEnvelope.Wrap(MessageKind.ToolResult, new ToolResultMessage
        {
            ClientId = "proto-1",
            ToolName = "get_property",
            IsError = false,
            Content = JsonSerializer.SerializeToElement(new { value = "Hello" }),
        }, correlationId: corr);

        var invokeBack = await SendAndReceive(invokeEnv, 4096);
        var resultBack = await SendAndReceive(resultEnv, 4096);

        // The hub matches a result to its request by the envelope correlation id.
        Assert.Equal(invokeBack.CorrelationId, resultBack.CorrelationId);
        Assert.Equal(MessageKind.InvokeTool, invokeBack.Kind);
        Assert.Equal(MessageKind.ToolResult, resultBack.Kind);
        Assert.Equal("Hello",
            resultBack.Unwrap<ToolResultMessage>()!.Content!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task MultipleMessages_StreamedBackToBack_DispatchByKind()
    {
        // The hub reads a connection as a stream of framed envelopes and dispatches
        // each on its Kind. Verify a heterogeneous burst reassembles in order.
        var messages = new[]
        {
            MessageEnvelope.Wrap(MessageKind.Register, new RegisterMessage { ClientId = "p" }),
            MessageEnvelope.Wrap(MessageKind.Heartbeat, new HeartbeatMessage { ClientId = "p", Sequence = 1 }),
            MessageEnvelope.Wrap(MessageKind.Heartbeat, new HeartbeatMessage { ClientId = "p", Sequence = 2 }),
            MessageEnvelope.Wrap(MessageKind.ClientDown, new ClientDownMessage { ClientId = "p", Graceful = true }),
        };

        using var wire = new MemoryStream();
        foreach (var m in messages)
            await FrameCodec.WriteAsync(wire, JsonSerializer.SerializeToUtf8Bytes(m, ProtocolJson.Options));
        wire.Position = 0;

        var kinds = new List<MessageKind>();
        long lastSeq = 0;
        while (await FrameCodec.TryReadAsync(wire) is { } framed)
        {
            var env = JsonSerializer.Deserialize<MessageEnvelope>(framed, ProtocolJson.Options)!;
            kinds.Add(env.Kind);
            if (env.Kind == MessageKind.Heartbeat)
                lastSeq = env.Unwrap<HeartbeatMessage>()!.Sequence;
        }

        Assert.Equal(
            new[] { MessageKind.Register, MessageKind.Heartbeat, MessageKind.Heartbeat, MessageKind.ClientDown },
            kinds);
        Assert.Equal(2, lastSeq);
    }
}
