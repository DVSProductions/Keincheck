using System.IO.Pipelines;
using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// A genuinely old client — one built against Keincheck.Client 0.9.0 — talking to this hub.
/// </summary>
/// <remarks>
/// <para>
/// This is the direction that matters most in practice. The hub auto-updates itself; apps do
/// not. So the overwhelmingly common mixed-version state in the wild is a new hub serving
/// clients nobody has rebuilt, and it has to keep working with no action from anyone.
/// </para>
/// <para>
/// "v1 client" here means precisely: advertises <c>ProtocolVersion = 1</c>, sends no Hello,
/// sends <see cref="ToolDescriptor"/>s with no <c>ReadOnly</c> field (it did not exist), and
/// understands none of the v2 message kinds.
/// </para>
/// </remarks>
public sealed class V1ClientCompatibilityTests
{
    private static (PipeChannel client, PipeChannel broker) DuplexPair()
    {
        var a = new Pipe();
        var b = new Pipe();
        return (
            new PipeChannel(new Duplex(b.Reader.AsStream(), a.Writer.AsStream())),
            new PipeChannel(new Duplex(a.Reader.AsStream(), b.Writer.AsStream())));
    }

    /// <summary>The catalog shape a v1 client sends: no ReadOnly on any descriptor.</summary>
    private static ToolDescriptor[] V1Catalog() =>
    [
        new() { Name = "get_logical_tree", Description = "read the tree" },
        new() { Name = "describe_screen", Description = "describe" },
        new() { Name = "click_at", Description = "click" },
    ];

    private sealed record Session(PipeChannel Client, Task Serve, CancellationTokenSource Cts, ClientInfo Info)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Client.DisposeAsync();
            try { await Serve; } catch { }
            Cts.Dispose();
        }
    }

    private static async Task<Session> ConnectV1Async(
        PipeClientBroker broker, string appId = "legacyapp", IReadOnlyList<ToolDescriptor>? tools = null)
    {
        var tcs = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected(object? _, ClientInfo i) => tcs.TrySetResult(i);
        broker.ClientConnected += OnConnected;
        try
        {
            var (client, brokerSide) = DuplexPair();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var serve = broker.AcceptChannel(brokerSide, cts.Token);   // the 2-arg overload a v1 caller used

            await client.SendAsync(MessageKind.Register, new RegisterMessage
            {
                ClientId = appId,
                DisplayName = appId,
                ProcessId = 0,
                ProtocolVersion = 1,        // the whole point
                OwnsWindows = true,
                // NOTE: no ClientVersion -- older clients did not report one.
            });
            await client.SendAsync(MessageKind.ToolList, new ToolListMessage
            {
                ClientId = appId,
                Tools = tools ?? V1Catalog(),
                OwnsWindows = true,
            });

            var info = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 250 && broker.ClientStatus(info.ClientId)?.Tools.Count is null or 0; i++)
                await Task.Delay(20);

            return new Session(client, serve, cts, broker.ClientStatus(info.ClientId)!);
        }
        finally { broker.ClientConnected -= OnConnected; }
    }

    private static PipeClientBroker NewBroker(KnownClientStore? store = null) => new(
        new BrokerOptions
        {
            WatchdogInterval = TimeSpan.FromHours(1),
            HeartbeatTimeout = TimeSpan.FromMinutes(5),
            InvokeTimeout = TimeSpan.FromSeconds(2),
        },
        store ?? KnownClientStore.Open(Path.Combine(Path.GetTempPath(), $"kc-v1-{Guid.NewGuid():N}.json")));

    // ---------------------------------------------------------------- it connects at all

    [Fact]
    public async Task A_v1_Client_Registers_And_Looks_Exactly_Like_It_Always_Did()
    {
        await using var broker = NewBroker();
        await using var session = await ConnectV1Async(broker);

        Assert.Equal("legacyapp#1", session.Info.ClientId);   // no @host, no change
        Assert.Equal("legacyapp", session.Info.AppId);
        Assert.Null(session.Info.Host);
        Assert.Equal(ClientTransport.Pipe, session.Info.Transport);
        Assert.True(session.Info.CanLaunch);
        Assert.False(session.Info.IsRemote);
        Assert.False(session.Info.ReadOnly);

        // ...and it auto-activates, as a local client always has.
        Assert.Equal("legacyapp#1", broker.ActiveClientId);
        Assert.Equal(3, session.Info.Tools.Count);
    }

    [Fact]
    public async Task A_v1_Client_Can_Be_Driven()
    {
        await using var broker = NewBroker();
        await using var session = await ConnectV1Async(broker);

        var invoke = broker.InvokeOnClientAsync(session.Info.ClientId, "get_logical_tree", null);

        // Answer as a v1 client would.
        var envelope = await session.Client.ReceiveAsync();
        Assert.Equal(MessageKind.InvokeTool, envelope!.Kind);
        await session.Client.SendAsync(MessageKind.ToolResult, new ToolResultMessage
        {
            ClientId = "legacyapp",
            ToolName = "get_logical_tree",
            Content = JsonSerializer.SerializeToElement(new[] { new { type = "text", text = "ok" } }),
        }, envelope.CorrelationId);

        var result = await invoke;
        Assert.False(result.IsError);
    }

    // ---------------------------------------------------------------- nothing v2 leaks to it

    [Fact]
    public async Task A_v1_Client_Never_Receives_A_Compressed_Frame()
    {
        // Compression is only enabled by the Hello capability exchange, which the pipe never
        // performs. A v1 client has no Brotli decoder path at all, so a compressed frame would
        // be an unrecoverable protocol error.
        await using var broker = NewBroker();
        await using var session = await ConnectV1Async(broker);

        // Force a payload far over the compression threshold.
        var big = new string('a', 512 * 1024);
        var invoke = broker.InvokeOnClientAsync(
            session.Info.ClientId, "get_logical_tree",
            JsonSerializer.SerializeToElement(new { blob = big }));

        var envelope = await session.Client.ReceiveAsync();
        Assert.NotNull(envelope);
        Assert.Equal(MessageKind.InvokeTool, envelope!.Kind);

        // The channel the hub wrote on must not have compression armed.
        Assert.False(session.Client.Limits.AllowCompression);

        await session.Client.SendAsync(MessageKind.ToolResult, new ToolResultMessage
        {
            ClientId = "legacyapp", ToolName = "get_logical_tree",
        }, envelope.CorrelationId);
        await invoke;
    }

    [Fact]
    public async Task A_v1_Client_Is_Never_Sent_A_v2_Message_Kind()
    {
        // Hello/Welcome/Rejected/EnrollResponse are remote-session or enrollment kinds. A v1
        // client would treat any of them as an unknown kind and ignore it -- but the hub must
        // not send one at all, and in particular must not send hub->client heartbeats to a
        // client that never negotiated them.
        await using var broker = NewBroker();
        await using var session = await ConnectV1Async(broker);

        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var unexpected = await session.Client.ReceiveAsync(quiet.Token);
            Assert.Fail($"the hub sent an unsolicited {unexpected?.Kind} to a v1 pipe client");
        }
        catch (OperationCanceledException)
        {
            // Correct: silence until the hub has something to ask for.
        }
    }

    // ---------------------------------------------------------------- the read-only fallback

    [Fact]
    public async Task A_v1_Catalog_Falls_Back_To_The_Name_Heuristic()
    {
        // A v1 ToolDescriptor has no ReadOnly, so the hub cannot defer to the client and uses
        // its own name heuristic -- which must behave exactly as it did before v2 existed.
        await using var broker = NewBroker();
        await using var session = await ConnectV1Async(broker);

        Assert.All(session.Info.Tools, t => Assert.Null(t.ReadOnly));

        broker.SetReadOnly(session.Info.ClientId, true);

        Assert.False(await RefusedAsync(broker, session, "get_logical_tree"));  // get_ => allowed
        Assert.True(await RefusedAsync(broker, session, "click_at"));           // mutating
        Assert.True(await RefusedAsync(broker, session, "something_unknown"));  // fail closed
    }

    /// <summary>Whether the read-only gate refused the call (as opposed to reaching the wire).</summary>
    private static async Task<bool> RefusedAsync(PipeClientBroker broker, Session session, string tool)
    {
        try
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await broker.InvokeOnClientAsync(session.Info.ClientId, tool, null, brief.Token);
            return false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("read-only", StringComparison.Ordinal))
        {
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (TimeoutException) { return false; }
    }

    // ---------------------------------------------------------------- identity stability

    [Theory]
    [InlineData("demo")]
    [InlineData("myapp")]
    [InlineData("Acme.WinBle")]
    [InlineData("probe123ab")]
    [InlineData("My-App_2")]
    [InlineData("Company.Product.Desktop")]
    public async Task Ordinary_App_Ids_Are_Not_Altered_By_The_New_Sanitiser(string appId)
    {
        // Sanitising the app id is new. It must not silently rename an existing app: the hub id
        // appears in recorded tests, in scripts, and in the persisted launch profile, so a
        // changed id is a broken setup for someone who did nothing but update their hub.
        await using var broker = NewBroker();
        await using var session = await ConnectV1Async(broker, appId);

        Assert.Equal($"{appId}#1", session.Info.ClientId);
        Assert.Equal(appId, session.Info.AppId);
    }

    [Fact]
    public async Task A_v1_Clients_Persisted_Profile_Still_Matches_After_The_Upgrade()
    {
        // known-clients.json written by an older hub has no Host field. It must load as a
        // LOCAL, launchable entry -- and the client must reclaim the same id and its persisted
        // read-only setting when it reconnects.
        var storePath = Path.Combine(Path.GetTempPath(), $"kc-v1-legacy-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(storePath, """
            [
              {
                "AppId": "legacyapp",
                "DisplayName": "Legacy App",
                "ExecutablePath": "C:\\Apps\\legacy.exe",
                "ReadOnly": true,
                "LastSeenUtc": "2026-01-01T00:00:00+00:00"
              }
            ]
            """);

        await using var broker = NewBroker(KnownClientStore.Open(storePath));

        // Visible before it ever connects, launchable, and read-only as recorded.
        var known = Assert.Single(broker.ListKnownClients());
        Assert.Equal("legacyapp#1", known.ClientId);
        Assert.True(known.CanLaunch);
        Assert.Null(known.Host);
        Assert.Equal(ClientTransport.Pipe, known.Transport);
        Assert.True(known.ReadOnly);
        Assert.Equal("C:\\Apps\\legacy.exe", known.ExecutablePath);

        // Reconnecting reclaims the same id and keeps the setting.
        await using var session = await ConnectV1Async(broker);
        Assert.Equal("legacyapp#1", session.Info.ClientId);
        Assert.True(session.Info.ReadOnly);
    }

    [Fact]
    public async Task A_v1_Client_Coexists_With_A_Remote_Client_Of_The_Same_App()
    {
        await using var broker = NewBroker();
        await using var legacy = await ConnectV1Async(broker, "myapp");

        var (remoteClient, remoteBrokerSide) = DuplexPair();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serve = broker.AcceptChannel(remoteBrokerSide, new ClientSessionContext
        {
            Transport = ClientTransport.Tcp,
            Host = "MACHINENAME",
            ReadOnlyDefault = true,
            CanLaunch = false,
        }, cts.Token);
        await remoteClient.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = "myapp", ProtocolVersion = ProtocolVersion.Current,
        });

        var remote = await broker.WaitForClientAsync("myapp@MACHINENAME", TimeSpan.FromSeconds(10));
        Assert.Equal("myapp@MACHINENAME#1", remote!.ClientId);
        Assert.Equal("myapp#1", legacy.Info.ClientId);

        // The v1 client keeps active; a remote one never steals it.
        Assert.Equal("myapp#1", broker.ActiveClientId);

        cts.Cancel();
        await remoteClient.DisposeAsync();
        try { await serve; } catch { }
        cts.Dispose();
    }

    private sealed class Duplex(Stream read, Stream write) : Stream
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
