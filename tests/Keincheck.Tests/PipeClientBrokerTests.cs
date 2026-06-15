using System.IO.Pipelines;
using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Tests;

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

    // ---- finding-1: auto-reselect on reconnect ----------------------------

    [Fact]
    public async Task Reconnect_Same_Id_Auto_Reclaims_Active_And_Fires_Update()
    {
        await using var broker = new PipeClientBroker(new BrokerOptions
        {
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            WatchdogInterval = TimeSpan.FromHours(1),
        }, KnownClientStore.Open(TempStorePath()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Connect: the first client auto-becomes active. (Auto-activate runs in a second
        // lock block AFTER the snapshot is published, so poll on ActiveClientId, not on
        // ClientStatus, to avoid the tiny window between the two.)
        var (c1, b1) = DuplexPair();
        var s1 = broker.AcceptChannel(b1, cts.Token);
        await c1.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "app", ProcessId = 42, ProtocolVersion = ProtocolVersion.Current });
        await WaitFor(() => broker.ActiveClientId == "app#1" ? "active" : null);
        Assert.Equal("app#1", broker.ActiveClientId);

        // Drop it. The broker observes the EOF, moves it to _seen, and clears _active while
        // remembering it as the auto-reselect target.
        await c1.DisposeAsync();
        try { await s1; } catch { }
        await WaitFor(() => broker.ActiveClientId is null ? "down" : null);
        Assert.Null(broker.ActiveClientId);
        Assert.DoesNotContain(broker.ListClients(), c => c.ClientId == "app#1");

        // Watch for the re-activation nudge (ClientUpdated) the auto-reselect fires.
        var reactivated = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.ClientUpdated += (_, info) =>
        {
            if (info.ClientId == "app#1" && info.IsConnected)
                reactivated.TrySetResult(info);
        };

        // Reconnect the SAME app/pid: it reclaims the same hub-id AND auto-becomes active.
        var (c2, b2) = DuplexPair();
        var s2 = broker.AcceptChannel(b2, cts.Token);
        await c2.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "app", ProcessId = 42, ProtocolVersion = ProtocolVersion.Current });

        var info = await reactivated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("app#1", info.ClientId);
        Assert.Equal("app#1", broker.ActiveClientId);

        cts.Cancel();
        await c2.DisposeAsync();
        try { await s2; } catch { }
    }

    [Fact]
    public async Task Manual_Selection_While_Down_Is_Not_Stolen_By_Reconnect()
    {
        await using var broker = new PipeClientBroker(new BrokerOptions
        {
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            WatchdogInterval = TimeSpan.FromHours(1),
        }, KnownClientStore.Open(TempStorePath()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Two clients connect. A auto-activates (first in).
        var (ca, ba) = DuplexPair();
        var sa = broker.AcceptChannel(ba, cts.Token);
        await ca.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "alpha", ProcessId = 1, ProtocolVersion = ProtocolVersion.Current });
        await WaitFor(() => broker.ClientStatus("alpha#1"));

        var (cb, bb) = DuplexPair();
        var sb = broker.AcceptChannel(bb, cts.Token);
        await cb.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "beta", ProcessId = 2, ProtocolVersion = ProtocolVersion.Current });
        await WaitFor(() => broker.ClientStatus("beta#1"));

        // Make alpha the deliberately-active client, then drop it.
        broker.ActiveClientId = "alpha#1";
        Assert.Equal("alpha#1", broker.ActiveClientId);
        await ca.DisposeAsync();
        try { await sa; } catch { }
        await WaitFor(() => broker.ActiveClientId is null ? "down" : null);

        // While alpha is down, the user manually selects beta.
        broker.ActiveClientId = "beta#1";
        Assert.Equal("beta#1", broker.ActiveClientId);

        // alpha reconnects: it must NOT steal active back from the deliberate beta selection.
        var (ca2, ba2) = DuplexPair();
        var sa2 = broker.AcceptChannel(ba2, cts.Token);
        await ca2.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "alpha", ProcessId = 1, ProtocolVersion = ProtocolVersion.Current });
        await WaitFor(() => broker.ListClients().Any(c => c.ClientId == "alpha#1") ? "back" : null);

        // beta stays active despite alpha's reconnect.
        Assert.Equal("beta#1", broker.ActiveClientId);

        cts.Cancel();
        await ca2.DisposeAsync();
        await cb.DisposeAsync();
        try { await sa2; } catch { }
        try { await sb; } catch { }
    }

    // ---- finding-1: WaitForClientAsync ------------------------------------

    [Fact]
    public async Task WaitForClient_Returns_Immediately_When_Already_Connected()
    {
        await using var broker = new PipeClientBroker(new BrokerOptions
        { WatchdogInterval = TimeSpan.FromHours(1) }, KnownClientStore.Open(TempStorePath()));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (c, b) = DuplexPair();
        var s = broker.AcceptChannel(b, cts.Token);
        await c.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "ready", ProcessId = 5, ProtocolVersion = ProtocolVersion.Current });
        await WaitFor(() => broker.ClientStatus("ready#1"));

        var info = await broker.WaitForClientAsync("ready", TimeSpan.FromSeconds(5), cts.Token);
        Assert.NotNull(info);
        Assert.Equal("ready#1", info!.ClientId);

        cts.Cancel();
        await c.DisposeAsync();
        try { await s; } catch { }
    }

    [Fact]
    public async Task WaitForClient_Blocks_Then_Resolves_On_Later_Connect()
    {
        await using var broker = new PipeClientBroker(new BrokerOptions
        { WatchdogInterval = TimeSpan.FromHours(1) }, KnownClientStore.Open(TempStorePath()));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start waiting BEFORE any client exists; the wait must block on the event.
        var waitTask = broker.WaitForClientAsync("late", TimeSpan.FromSeconds(8), cts.Token);
        Assert.False(waitTask.IsCompleted, "wait should block until the client connects");

        var (c, b) = DuplexPair();
        var s = broker.AcceptChannel(b, cts.Token);
        await c.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "late", ProcessId = 6, ProtocolVersion = ProtocolVersion.Current });

        var info = await waitTask;
        Assert.NotNull(info);
        Assert.Equal("late#1", info!.ClientId);

        cts.Cancel();
        await c.DisposeAsync();
        try { await s; } catch { }
    }

    [Fact]
    public async Task WaitForClient_Times_Out_To_Null_When_None_Appears()
    {
        await using var broker = new PipeClientBroker(new BrokerOptions
        { WatchdogInterval = TimeSpan.FromHours(1) }, KnownClientStore.Open(TempStorePath()));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var info = await broker.WaitForClientAsync("ghost", TimeSpan.FromMilliseconds(200), cts.Token);
        Assert.Null(info);
    }

    // ---- finding-2: same-pid reconnect dedup ------------------------------

    [Fact]
    public async Task Reconnect_Same_Pid_Yields_A_Single_Live_Id()
    {
        await using var broker = new PipeClientBroker(new BrokerOptions
        {
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            WatchdogInterval = TimeSpan.FromHours(1),
        }, KnownClientStore.Open(TempStorePath()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // First registration of pid=20 -> twin#1.
        var (c1, b1) = DuplexPair();
        var s1 = broker.AcceptChannel(b1, cts.Token);
        await c1.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "twin", ProcessId = 20, ProtocolVersion = ProtocolVersion.Current });
        await WaitFor(() => broker.ClientStatus("twin#1"));

        // The SAME process re-registers (auto-reconnect storm) on a SECOND channel while the
        // first session is still considered live: dedup must reuse twin#1, not mint twin#2.
        var (c2, b2) = DuplexPair();
        var s2 = broker.AcceptChannel(b2, cts.Token);
        await c2.SendAsync(MessageKind.Register, new RegisterMessage
        { ClientId = "twin", ProcessId = 20, ProtocolVersion = ProtocolVersion.Current });

        // Give the supersede a moment to evict the stale session, then assert a single id.
        await WaitFor(() => broker.ListClients().Count == 1 ? "one" : null);
        var ids = broker.ListClients().Select(c => c.ClientId).ToList();
        Assert.Equal(new[] { "twin#1" }, ids);

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
