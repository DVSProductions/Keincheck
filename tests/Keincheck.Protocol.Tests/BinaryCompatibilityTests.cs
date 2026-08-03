using System.Reflection;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Protocol.Tests;

/// <summary>
/// Pins the shapes that an already-published <c>Keincheck.Protocol</c> consumer is compiled
/// against.
/// </summary>
/// <remarks>
/// Adding an optional parameter to an existing method or constructor is source-compatible and
/// <b>not</b> binary-compatible: the signature is baked into the caller's assembly, so an app
/// built against the published package fails at runtime with <c>MissingMethodException</c>
/// rather than at build time with something explanatory. These tests assert the original
/// signatures still exist, so the mistake is caught here instead of in someone's app.
/// </remarks>
public class BinaryCompatibilityTests
{
    [Fact]
    public void FrameCodec_Keeps_Its_Original_Write_Signature()
    {
        // void Write(Stream, ReadOnlySpan<byte>, int)
        var method = typeof(FrameCodec).GetMethod(
            nameof(FrameCodec.Write),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(Stream), typeof(ReadOnlySpan<byte>), typeof(int)],
            modifiers: null);

        Assert.NotNull(method);
    }

    [Fact]
    public void FrameCodec_Keeps_Its_Original_WriteAsync_Signature()
    {
        // Task WriteAsync(Stream, ReadOnlyMemory<byte>, int, CancellationToken)
        var method = typeof(FrameCodec).GetMethod(
            nameof(FrameCodec.WriteAsync),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(Stream), typeof(ReadOnlyMemory<byte>), typeof(int), typeof(CancellationToken)],
            modifiers: null);

        Assert.NotNull(method);
    }

    [Fact]
    public void PipeChannel_Keeps_Its_Original_Constructor_Signature()
    {
        // PipeChannel(Stream, bool)
        var ctor = typeof(PipeChannel).GetConstructor([typeof(Stream), typeof(bool)]);
        Assert.NotNull(ctor);
    }

    [Fact]
    public void The_Original_Overloads_Still_Behave_As_Before()
    {
        // Not just present -- still uncompressed and still default-limited, which is what a
        // pre-v2 caller is entitled to assume.
        var payload = new byte[4096];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)'a';   // very compressible, to prove it is NOT compressed

        using var ms = new MemoryStream();
        FrameCodec.Write(ms, payload, FrameCodec.DefaultMaxChunkPayload);

        Assert.Equal(0, ms.ToArray()[4] & 0x02);                       // compression flag clear
        Assert.Equal(payload.Length + FrameCodec.ChunkHeaderSize, ms.Length);

        using var channel = new PipeChannel(new MemoryStream(), ownsStream: true);
        Assert.Same(ChannelLimits.Default, channel.Limits);
    }

    [Fact]
    public void A_Pipe_Client_Advertises_v1_So_An_Older_Hub_Still_Accepts_It()
    {
        // The protocol bump must not turn a client-package update into an outage. Nothing in a
        // pipe session uses a v2 feature, so a v2 client advertises v1 there and an older hub
        // -- which computes IsCompatible(2) == false and drops the connection WITHOUT telling
        // the client why -- keeps working.
        Assert.Equal(1, ProtocolVersion.Minimum);
        Assert.True(ProtocolVersion.IsCompatible(ProtocolVersion.Minimum));

        // The interface default is what the pipe connector inherits.
        var advertised = ((IChannelConnector)new StubConnector()).AdvertisedProtocolVersion;
        Assert.Equal(ProtocolVersion.Minimum, advertised);
    }

    private sealed class StubConnector : IChannelConnector
    {
        public Task<ChannelSession> ConnectAsync(ChannelConnectContext context, CancellationToken ct) =>
            throw new NotSupportedException();

        public string Describe() => "stub";
    }
}
