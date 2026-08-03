using System.IO.Pipelines;
using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Tests for the hub's read-only tool gate.
/// </summary>
/// <remarks>
/// The gate used to be a bare name allow-list that claimed, in its own comment, to prefer
/// the client's annotation — but never read one. It therefore refused genuinely
/// side-effect-free tools whose names it did not recognise. That was survivable while
/// read-only was a rarely-used opt-in toggle. It stops being survivable once remote clients
/// default to read-only, because then the broken path is the <i>default</i> path: "just look
/// at the suit" would fail on <c>describe_screen</c> and <c>wait_for_idle</c>.
/// </remarks>
public sealed class ReadOnlyGateTests
{
    private static (PipeChannel client, PipeChannel broker) DuplexPair()
    {
        var clientToBroker = new Pipe();
        var brokerToClient = new Pipe();
        return (
            new PipeChannel(new DuplexStream(brokerToClient.Reader.AsStream(), clientToBroker.Writer.AsStream())),
            new PipeChannel(new DuplexStream(clientToBroker.Reader.AsStream(), brokerToClient.Writer.AsStream())));
    }

    /// <summary>The real Core tool names, with the classification the client actually reports.</summary>
    private static ToolDescriptor[] CoreTools() =>
    [
        // Read-only, and the old name heuristic got these RIGHT.
        new() { Name = "get_logical_tree", ReadOnly = true },
        new() { Name = "list_windows", ReadOnly = true },
        new() { Name = "hit_test", ReadOnly = true },
        new() { Name = "screenshot_marked", ReadOnly = true },

        // Read-only, and the old name heuristic got these WRONG.
        new() { Name = "describe_screen", ReadOnly = true },
        new() { Name = "wait_for_idle", ReadOnly = true },   // the list matched "wait_for" exactly
        new() { Name = "keincheck_guide", ReadOnly = true },
        new() { Name = "ScreenshotWindow", ReadOnly = true },

        // Genuinely mutating.
        new() { Name = "set_property", ReadOnly = false },
        new() { Name = "click_at", ReadOnly = false },
        new() { Name = "type_text", ReadOnly = false },
    ];

    private sealed record Harness(
        PipeClientBroker Broker, PipeChannel Client, Task Serve, CancellationTokenSource Cts) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Client.DisposeAsync();
            try { await Serve; } catch { /* cancellation */ }
            await Broker.DisposeAsync();
            Cts.Dispose();
        }
    }

    /// <summary>Registers a client, publishes <paramref name="tools"/>, and marks it read-only.</summary>
    private static async Task<Harness> ConnectReadOnlyAsync(IReadOnlyList<ToolDescriptor> tools)
    {
        var broker = new PipeClientBroker(
            new BrokerOptions
            {
                HeartbeatTimeout = TimeSpan.FromSeconds(30),
                WatchdogInterval = TimeSpan.FromHours(1),
                InvokeTimeout = TimeSpan.FromSeconds(5),
            },
            KnownClientStore.Open(Path.Combine(Path.GetTempPath(), $"avmcp-ro-{Guid.NewGuid():N}.json")));

        var (client, brokerSide) = DuplexPair();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serve = broker.AcceptChannel(brokerSide, cts.Token);

        await client.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = "demo", DisplayName = "Demo", ProcessId = 0,
            ProtocolVersion = ProtocolVersion.Current,
        });
        await client.SendAsync(MessageKind.ToolList, new ToolListMessage { ClientId = "demo", Tools = tools });

        // Wait for the catalog to land, then flip the read-only switch.
        for (var i = 0; i < 250 && broker.ClientStatus("demo#1")?.Tools.Count is null or 0; i++)
            await Task.Delay(20);
        broker.SetReadOnly("demo#1", true);

        return new Harness(broker, client, serve, cts);
    }

    /// <summary>Runs the tool and reports whether the read-only gate refused it.</summary>
    private static async Task<bool> RefusedAsync(PipeClientBroker broker, string toolName)
    {
        try
        {
            // A 250ms budget: a refusal is synchronous, whereas an ACCEPTED call reaches the
            // wire and then times out because this harness never answers. Either way we
            // learn what we came for, and only the refusal path is fast.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await broker.InvokeOnClientAsync("demo#1", toolName, null, cts.Token);
            return false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("read-only", StringComparison.Ordinal))
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false; // reached the wire => the gate allowed it
        }
        catch (TimeoutException)
        {
            return false; // ditto
        }
    }

    [Theory]
    // The old heuristic already allowed these.
    [InlineData("get_logical_tree")]
    [InlineData("list_windows")]
    [InlineData("hit_test")]
    [InlineData("screenshot_marked")]
    // The old heuristic REFUSED these, wrongly. This is the regression the fix closes.
    [InlineData("describe_screen")]
    [InlineData("wait_for_idle")]
    [InlineData("keincheck_guide")]
    [InlineData("ScreenshotWindow")]
    public async Task ReadOnly_Client_Allows_Every_Tool_The_Client_Declared_ReadOnly(string toolName)
    {
        await using var h = await ConnectReadOnlyAsync(CoreTools());
        Assert.False(await RefusedAsync(h.Broker, toolName),
            $"'{toolName}' is declared read-only by the client and must not be refused.");
    }

    [Theory]
    [InlineData("set_property")]
    [InlineData("click_at")]
    [InlineData("type_text")]
    public async Task ReadOnly_Client_Still_Refuses_Declared_Mutating_Tools(string toolName)
    {
        await using var h = await ConnectReadOnlyAsync(CoreTools());
        Assert.True(await RefusedAsync(h.Broker, toolName));
    }

    [Fact]
    public async Task Client_Declaration_Overrides_The_Name_Heuristic_In_Both_Directions()
    {
        // The client is the authority. A get_-prefixed tool it declares mutating must be
        // refused, and a scary-sounding tool it declares read-only must be allowed --
        // otherwise the name heuristic is silently still in charge.
        await using var h = await ConnectReadOnlyAsync(
        [
            new ToolDescriptor { Name = "get_and_delete_everything", ReadOnly = false },
            new ToolDescriptor { Name = "detonate", ReadOnly = true },
        ]);

        Assert.True(await RefusedAsync(h.Broker, "get_and_delete_everything"));
        Assert.False(await RefusedAsync(h.Broker, "detonate"));
    }

    [Fact]
    public async Task Undeclared_Tools_Fall_Back_To_The_FailClosed_Heuristic()
    {
        // A v1 client predates the field entirely: ReadOnly is null on every descriptor and
        // the hub must still behave exactly as it did before.
        await using var h = await ConnectReadOnlyAsync(
        [
            new ToolDescriptor { Name = "get_logical_tree" },
            new ToolDescriptor { Name = "set_property" },
        ]);

        Assert.False(await RefusedAsync(h.Broker, "get_logical_tree"));
        Assert.True(await RefusedAsync(h.Broker, "set_property"));

        // And a name the hub has never heard of stays refused rather than being waved through.
        Assert.True(await RefusedAsync(h.Broker, "something_brand_new"));
    }

    /// <summary>A read/write stream stitched from two half-duplex streams.</summary>
    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] b, int o, int c) => read.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => read.ReadAsync(b, ct);
        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => read.ReadAsync(b, o, c, ct);

        public override void Write(byte[] b, int o, int c) => write.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => write.WriteAsync(b, ct);
        public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => write.WriteAsync(b, o, c, ct);

        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { read.Dispose(); write.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
