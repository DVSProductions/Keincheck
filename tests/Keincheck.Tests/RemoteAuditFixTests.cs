using System.IO.Pipelines;
using Keincheck.Hub;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Regression tests for defects found by auditing the remote work. Each one is a bug that
/// shipped in the first draft and was reachable in normal use, so each gets a test that fails
/// against the original code.
/// </summary>
public sealed class RemoteAuditFixTests
{
    private static readonly ClientSessionContext Remote = new()
    {
        Transport = ClientTransport.Tcp,
        Host = "MACHINENAME",
        ReadOnlyDefault = true,
        CanLaunch = false,
    };

    private static (PipeChannel client, PipeChannel broker) DuplexPair()
    {
        var a = new Pipe();
        var b = new Pipe();
        return (
            new PipeChannel(new Duplex(b.Reader.AsStream(), a.Writer.AsStream())),
            new PipeChannel(new Duplex(a.Reader.AsStream(), b.Writer.AsStream())));
    }

    private static PipeClientBroker NewBroker(KnownClientStore? store = null) => new(
        new BrokerOptions
        {
            WatchdogInterval = TimeSpan.FromHours(1),
            HeartbeatTimeout = TimeSpan.FromMinutes(5),
            InvokeTimeout = TimeSpan.FromSeconds(2),
        },
        store ?? KnownClientStore.Open(Path.Combine(Path.GetTempPath(), $"kc-fix-{Guid.NewGuid():N}.json")));

    private sealed record Session(PipeChannel Channel, Task Serve, CancellationTokenSource Cts, ClientInfo Info)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Channel.DisposeAsync();
            try { await Serve; } catch { }
            Cts.Dispose();
        }
    }

    private static async Task<Session> ConnectAsync(
        PipeClientBroker broker, string appId, ClientSessionContext context)
    {
        var tcs = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected(object? _, ClientInfo i) => tcs.TrySetResult(i);
        broker.ClientConnected += OnConnected;
        try
        {
            var (client, brokerSide) = DuplexPair();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var serve = broker.AcceptChannel(brokerSide, context, cts.Token);
            await client.SendAsync(MessageKind.Register, new RegisterMessage
            {
                ClientId = appId, ProtocolVersion = ProtocolVersion.Current,
            });
            var info = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new Session(client, serve, cts, info);
        }
        finally { broker.ClientConnected -= OnConnected; }
    }

    // ---------------------------------------------------------------- id stability

    [Fact]
    public async Task A_Remote_Client_Keeps_Its_Id_Across_Reconnects()
    {
        // The suffix was reserved under the composite key ("myapp@HOST") but released under
        // the bare app id, so the slot leaked and the id climbed #1, #2, #3... on every
        // reconnect. On the motivating target -- a mobile device on wifi -- that means the
        // operator's hub_select_client breaks every time the link blips, and _seen grows
        // without bound over the months-long runtime this is designed for.
        await using var broker = NewBroker();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var session = await ConnectAsync(broker, "myapp", Remote);
            Assert.Equal("myapp@MACHINENAME#1", session.Info.ClientId);
            await session.DisposeAsync();

            // Wait for the disconnect to land before reconnecting.
            for (var i = 0; i < 200 && broker.ListClients().Count > 0; i++)
                await Task.Delay(20);
        }

        // And the registry did not accumulate a ghost per cycle.
        Assert.Single(broker.ListKnownClients());
    }

    [Fact]
    public async Task A_Remote_Disconnect_Does_Not_Free_A_Local_Apps_Slot()
    {
        // Releasing under the bare app id also removed slot 1 from an unrelated LOCAL app's
        // reservation set while that app was still live.
        await using var broker = NewBroker();
        await using var local = await ConnectAsync(broker, "myapp", ClientSessionContext.LocalPipe);
        Assert.Equal("myapp#1", local.Info.ClientId);

        var remote = await ConnectAsync(broker, "myapp", Remote);
        await remote.DisposeAsync();
        for (var i = 0; i < 200 && broker.ListClients().Count > 1; i++)
            await Task.Delay(20);

        // A second local instance must still get #2, not collide on #1.
        await using var second = await ConnectAsync(broker, "myapp", ClientSessionContext.LocalPipe);
        Assert.Equal("myapp#2", second.Info.ClientId);
    }

    // ---------------------------------------------------------------- app id

    [Theory]
    [InlineData("myapp@MACHINENAME", "myapp_MACHINENAME")]
    [InlineData("evil#9", "evil_9")]
    [InlineData("has space", "has_space")]
    [InlineData("", "avalonia-app")]
    [InlineData("   ", "avalonia-app")]
    public void A_Client_Chosen_AppId_Is_Sanitized(string reported, string expected)
    {
        Assert.Equal(expected, PipeClientBroker.SanitizeAppId(reported));
    }

    [Fact]
    public void An_Overlong_AppId_Is_Truncated()
    {
        // It becomes a key in three dictionaries; unbounded is a free way to bloat them.
        var sanitized = PipeClientBroker.SanitizeAppId(new string('a', 10_000));
        Assert.Equal(PipeClientBroker.MaxAppIdLength, sanitized.Length);
    }

    [Fact]
    public async Task A_Remote_Client_Cannot_Impersonate_The_AppAtHost_Disambiguator()
    {
        // The attack: a client holding a credential for host EVIL registers with the app id
        // "myapp@MACHINENAME". Matches() compares a wait-filter against AppId verbatim, so
        // hub_wait_for_client { appId: "myapp@MACHINENAME" } would have returned the
        // impostor -- and the operator's subsequent tool calls, arguments included, with it.
        // The host half was never spoofable (it comes from the validated certificate); this is
        // the other half.
        await using var broker = NewBroker();
        await using var real = await ConnectAsync(broker, "myapp", Remote);
        await using var impostor = await ConnectAsync(
            broker, "myapp@MACHINENAME", Remote with { Host = "EVIL" });

        Assert.Equal("myapp@MACHINENAME#1", real.Info.ClientId);
        Assert.DoesNotContain("@MACHINENAME#", impostor.Info.AppId!);

        var found = await broker.WaitForClientAsync("myapp@MACHINENAME", TimeSpan.FromSeconds(5));
        Assert.Equal(real.Info.ClientId, found!.ClientId);
        Assert.Equal("MACHINENAME", found.Host);
    }

    // ---------------------------------------------------------------- registration

    [Fact]
    public async Task A_Second_Register_On_One_Session_Is_Refused()
    {
        // Unbounded registrations on a single authenticated session minted a LiveClient per
        // frame, and suffix allocation probes the live set linearly while holding the registry
        // lock -- so a few thousand frames stall every other caller for seconds at a time.
        await using var broker = NewBroker();
        var (client, brokerSide) = DuplexPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serve = broker.AcceptChannel(brokerSide, Remote, cts.Token);

        for (var i = 0; i < 25; i++)
        {
            try
            {
                await client.SendAsync(MessageKind.Register, new RegisterMessage
                {
                    ClientId = "myapp", ProtocolVersion = ProtocolVersion.Current,
                }, cancellationToken: cts.Token);
            }
            catch { break; /* the hub tore the session down, which is the point */ }
            await Task.Delay(20, cts.Token);
        }

        // At most one client, ever -- not 25.
        Assert.True(broker.ListClients().Count <= 1, $"got {broker.ListClients().Count} clients from one session");

        cts.Cancel();
        await client.DisposeAsync();
        try { await serve; } catch { }
    }

    // ---------------------------------------------------------------- launch safety

    [Fact]
    public async Task Launching_By_BARE_App_Id_Cannot_Start_A_Local_Copy_Of_A_Remote_App()
    {
        // The guard checked the exact hub id, so the bare form -- which the guide and the wait
        // filters actively encourage -- missed both dictionaries, fell through to the LOCAL
        // launch profile, and started a local copy while the only connected 'myapp' was
        // the remote one. Exactly the silent-and-plausible failure the guard exists to stop,
        // reached by the most natural spelling of the request.
        var storePath = Path.Combine(Path.GetTempPath(), $"kc-bare-{Guid.NewGuid():N}.json");
        var store = KnownClientStore.Open(storePath);
        store.Upsert(new KnownClientProfile
        {
            AppId = "myapp",
            ExecutablePath = Environment.ProcessPath ?? "C:\\Windows\\System32\\cmd.exe",
            LastSeenUtc = DateTimeOffset.UtcNow,
        });

        await using var broker = NewBroker(store);
        await using var remote = await ConnectAsync(broker, "myapp", Remote);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.LaunchClientAsync("myapp"));
        Assert.Contains("cannot start or stop processes", ex.Message);
    }

    [Fact]
    public async Task Launching_By_Bare_App_Id_Still_Works_When_A_Local_Client_Owns_It()
    {
        // The refusal must be scoped to "this id resolves ONLY to remote clients" -- a local
        // app of the same name is still launchable, which is the pre-existing behaviour.
        var storePath = Path.Combine(Path.GetTempPath(), $"kc-bare2-{Guid.NewGuid():N}.json");
        var store = KnownClientStore.Open(storePath);
        store.Upsert(new KnownClientProfile
        {
            AppId = "myapp", ExecutablePath = null, LastSeenUtc = DateTimeOffset.UtcNow,
        });

        await using var broker = NewBroker(store);
        await using var local = await ConnectAsync(broker, "myapp", ClientSessionContext.LocalPipe);
        await using var remote = await ConnectAsync(broker, "myapp", Remote);

        // Reaches the profile lookup (and fails there for want of a path) rather than being
        // refused as remote.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.LaunchClientAsync("myapp"));
        Assert.DoesNotContain("cannot start or stop processes", ex.Message);
    }

    [Theory]
    [InlineData("myapp@MACHINENAME")]
    [InlineData("MYAPP@machinename")]
    public async Task Launching_By_The_AppId_At_Host_Spelling_Is_Refused_As_Remote(string spelling)
    {
        // The third spelling, and the one that skipped the guard entirely. The registry is keyed
        // "myapp@MACHINENAME#1", so this misses both dictionaries; and the fallback scan
        // compared only the BARE app id, which this is not either. So `known` stayed null and
        // EnsureLaunchable returned without deciding anything.
        //
        // It could not actually start a local process -- SanitizeAppId maps '@' to '_', so no
        // local profile can ever be keyed with one. What shipped was the wrong ERROR: the
        // operator got "has no recorded executable path to launch", which reads as "record a
        // path for it", when the truth is "that process is on another machine". Every other
        // clientId-taking tool rejects this spelling cleanly; launch was the odd one out.
        var storePath = Path.Combine(Path.GetTempPath(), $"kc-athost-{Guid.NewGuid():N}.json");
        var store = KnownClientStore.Open(storePath);

        await using var broker = NewBroker(store);
        await using var remote = await ConnectAsync(broker, "myapp", Remote);

        var launch = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.LaunchClientAsync(spelling));
        Assert.Contains("cannot start or stop processes", launch.Message);
        Assert.DoesNotContain("no recorded executable path", launch.Message);

        // Restart takes the same guard and must answer the same way.
        var restart = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.RestartClientAsync(spelling));
        Assert.Contains("cannot start or stop processes", restart.Message);
    }

    [Fact]
    public async Task The_Refusal_Names_The_Machine_The_App_Is_Actually_On()
    {
        // The message is the whole point of the fix, so assert the operator is pointed at the
        // right machine rather than merely refused.
        var storePath = Path.Combine(Path.GetTempPath(), $"kc-athost2-{Guid.NewGuid():N}.json");
        await using var broker = NewBroker(KnownClientStore.Open(storePath));
        await using var remote = await ConnectAsync(broker, "myapp", Remote);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.LaunchClientAsync("myapp@MACHINENAME"));
        Assert.Contains("MACHINENAME", ex.Message);
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
