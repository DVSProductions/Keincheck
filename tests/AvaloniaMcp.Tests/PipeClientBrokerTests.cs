using System.IO.Pipelines;
using System.Text.Json;
using AvaloniaMcp.Hub;
using AvaloniaMcp.Protocol;
using Xunit;

namespace AvaloniaMcp.Tests;

/// <summary>
/// Unit tests for the live <see cref="PipeClientBroker"/> driven over an in-memory
/// duplex stream pair (a stand-in for the named pipe), so the registry, id assignment,
/// and InvokeTool↔ToolResult correlation are proven without a real pipe or a UI thread.
/// </summary>
public sealed class PipeClientBrokerTests
{
    // Builds a connected (clientChannel, brokerChannel) pair over two in-memory pipes.
    private static (PipeChannel client, PipeChannel broker) DuplexPair()
    {
        var clientToBroker = new Pipe();
        var brokerToClient = new Pipe();

        var brokerChannel = new PipeChannel(
            new DuplexStream(clientToBroker.Reader.AsStream(), brokerToClient.Writer.AsStream()));
        var clientChannel = new PipeChannel(
            new DuplexStream(brokerToClient.Reader.AsStream(), clientToBroker.Writer.AsStream()));
        return (clientChannel, brokerChannel);
    }

    [Fact]
    public async Task Register_Then_ToolList_AppearsInRegistry_AndBecomesActive()
    {
        await using var broker = new PipeClientBroker(new BrokerOptions
        {
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            WatchdogInterval = TimeSpan.FromHours(1), // keep the watchdog out of the way
        }, KnownClientStore.Open(TempStorePath()));

        var connected = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.ClientConnected += (_, info) => connected.TrySetResult(info);

        var (client, brokerSide) = DuplexPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = broker.AcceptChannel(brokerSide, cts.Token);

        // Client registers, then reports a tool.
        await client.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = "demo", DisplayName = "Demo App", ProcessId = 4321,
            ProtocolVersion = ProtocolVersion.Current,
        });

        var info = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("demo#1", info.ClientId);
        Assert.Equal("demo", info.AppId);
        Assert.True(info.IsConnected);

        // First client auto-becomes active.
        Assert.Equal("demo#1", broker.ActiveClientId);

        await client.SendAsync(MessageKind.ToolList, new ToolListMessage
        {
            ClientId = "demo",
            Tools = new[]
            {
                new ToolDescriptor { Name = "get_logical_tree", Description = "tree" },
            },
        });

        // Poll the registry until the tool list lands.
        var status = await WaitFor(() =>
        {
            var s = broker.ClientStatus("demo#1");
            return s is { Tools.Count: > 0 } ? s : null;
        });
        Assert.Equal("get_logical_tree", Assert.Single(status!.Tools).Name);
        Assert.Contains(broker.ListClients(), c => c.ClientId == "demo#1");

        cts.Cancel();
        await client.DisposeAsync();
        try { await serve; } catch { /* cancellation */ }
    }

    [Fact]
    public async Task InvokeOnClient_Correlates_RequestAndResponse()
    {
        await using var broker = new PipeClientBroker(new BrokerOptions
        {
            WatchdogInterval = TimeSpan.FromHours(1),
            InvokeTimeout = TimeSpan.FromSeconds(5),
        }, KnownClientStore.Open(TempStorePath()));

        var (client, brokerSide) = DuplexPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = broker.AcceptChannel(brokerSide, cts.Token);

        await client.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = "demo", ProcessId = 1, ProtocolVersion = ProtocolVersion.Current,
        });
        await WaitFor(() => broker.ClientStatus("demo#1"));

        // Simulate the client: echo any InvokeTool back as a successful ToolResult.
        var clientLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var env = await client.ReceiveAsync(cts.Token);
                if (env is null) break;
                if (env.Kind != MessageKind.InvokeTool) continue;

                var invoke = env.Unwrap<InvokeToolMessage>()!;
                var content = JsonSerializer.SerializeToElement(
                    new object[] { new { type = "text", text = $"ran {invoke.ToolName}" } },
                    ProtocolJson.Options);
                await client.SendAsync(MessageKind.ToolResult, new ToolResultMessage
                {
                    ClientId = invoke.ClientId,
                    ToolName = invoke.ToolName,
                    IsError = false,
                    Content = content,
                }, env.CorrelationId, cts.Token);
            }
        });

        var args = JsonDocument.Parse("""{"maxDepth":3}""").RootElement;
        var result = await broker.InvokeOnClientAsync("demo#1", "get_logical_tree", args, cts.Token);

        Assert.False(result.IsError);
        Assert.Equal("get_logical_tree", result.ToolName);
        Assert.NotNull(result.Content);
        Assert.Contains("ran get_logical_tree", result.Content!.Value.GetRawText());

        cts.Cancel();
        await client.DisposeAsync();
        try { await serve; } catch { }
        try { await clientLoop; } catch { }
    }

    [Fact]
    public async Task SecondInstance_OfSameApp_GetsDistinctSuffix()
    {
        await using var broker = new PipeClientBroker(new BrokerOptions
        {
            WatchdogInterval = TimeSpan.FromHours(1),
        }, KnownClientStore.Open(TempStorePath()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (c1, b1) = DuplexPair();
        var s1 = broker.AcceptChannel(b1, cts.Token);
        await c1.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "twin", ProcessId = 10, ProtocolVersion = ProtocolVersion.Current });
        await WaitFor(() => broker.ClientStatus("twin#1"));

        var (c2, b2) = DuplexPair();
        var s2 = broker.AcceptChannel(b2, cts.Token);
        await c2.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "twin", ProcessId = 11, ProtocolVersion = ProtocolVersion.Current });
        await WaitFor(() => broker.ClientStatus("twin#2"));

        var ids = broker.ListClients().Select(c => c.ClientId).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "twin#1", "twin#2" }, ids);

        cts.Cancel();
        await c1.DisposeAsync();
        await c2.DisposeAsync();
        try { await s1; } catch { }
        try { await s2; } catch { }
    }

    // ---- helpers ----------------------------------------------------------

    private static string TempStorePath()
        => Path.Combine(Path.GetTempPath(), $"avmcp-test-{Guid.NewGuid():N}.json");

    private static async Task<T?> WaitFor<T>(Func<T?> probe, int timeoutMs = 5000) where T : class
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var v = probe();
            if (v is not null) return v;
            await Task.Delay(20);
        }
        return probe();
    }

    /// <summary>A read/write stream stitched from two half-duplex streams.</summary>
    private sealed class DuplexStream : Stream
    {
        private readonly Stream _read;
        private readonly Stream _write;
        public DuplexStream(Stream read, Stream write) { _read = read; _write = write; }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _read.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _read.ReadAsync(buffer, offset, count, ct);

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _write.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _write.WriteAsync(buffer, offset, count, ct);

        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken ct) => _write.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _read.Dispose(); _write.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
