using System.IO.Pipelines;
using Keincheck.Hub;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Tests for how the broker treats a session the listener has marked as remote.
/// </summary>
/// <remarks>
/// Driven over an in-memory duplex pair rather than real TLS: the transport is proven
/// elsewhere, and what matters here is that a <see cref="ClientSessionContext"/> saying
/// "this client is on another machine" actually changes the registry's behaviour.
/// </remarks>
public sealed class RemoteBrokerTests
{
    private static readonly ClientSessionContext RemoteContext = new()
    {
        Transport = ClientTransport.Tcp,
        Host = "MACHINENAME",
        PeerAddress = "127.0.0.1",
        ReadOnlyDefault = true,
        CanLaunch = false,
    };

    private static (PipeChannel client, PipeChannel broker) DuplexPair()
    {
        var clientToBroker = new Pipe();
        var brokerToClient = new Pipe();
        return (
            new PipeChannel(new Duplex(brokerToClient.Reader.AsStream(), clientToBroker.Writer.AsStream())),
            new PipeChannel(new Duplex(clientToBroker.Reader.AsStream(), brokerToClient.Writer.AsStream())));
    }

    private static PipeClientBroker NewBroker() => new(
        new BrokerOptions
        {
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            WatchdogInterval = TimeSpan.FromHours(1),
            InvokeTimeout = TimeSpan.FromSeconds(2),
        },
        KnownClientStore.Open(Path.Combine(Path.GetTempPath(), $"avmcp-remote-{Guid.NewGuid():N}.json")));

    /// <summary>Registers a client and waits for the broker to record it.</summary>
    private static async Task<(PipeChannel Channel, Task Serve, CancellationTokenSource Cts, ClientInfo Info)>
        RegisterAsync(PipeClientBroker broker, string appId, ClientSessionContext context, int processId = 0)
    {
        var connected = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected(object? _, ClientInfo i)
        {
            if (string.Equals(i.AppId, appId, StringComparison.Ordinal))
                connected.TrySetResult(i);
        }

        broker.ClientConnected += OnConnected;
        try
        {
            var (client, brokerSide) = DuplexPair();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var serve = broker.AcceptChannel(brokerSide, context, cts.Token);

            await client.SendAsync(MessageKind.Register, new RegisterMessage
            {
                ClientId = appId,
                DisplayName = appId,
                ProcessId = processId,
                ProtocolVersion = ProtocolVersion.Current,
            });

            var info = await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return (client, serve, cts, info);
        }
        finally
        {
            broker.ClientConnected -= OnConnected;
        }
    }

    // ---------------------------------------------------------------- identity

    [Fact]
    public async Task A_Remote_Client_Is_Filed_Under_AppId_At_Host()
    {
        await using var broker = NewBroker();
        var (client, serve, cts, info) = await RegisterAsync(broker, "myapp", RemoteContext);

        Assert.Equal("myapp@MACHINENAME#1", info.ClientId);
        Assert.Equal("myapp", info.AppId);
        Assert.Equal("MACHINENAME", info.Host);
        Assert.Equal(ClientTransport.Tcp, info.Transport);
        Assert.True(info.IsRemote);

        await Cleanup(client, serve, cts);
    }

    [Fact]
    public async Task A_Local_Client_Id_Is_Completely_Unchanged()
    {
        // The compatibility guarantee: a purely local setup must look exactly as it did.
        await using var broker = NewBroker();
        var (client, serve, cts, info) = await RegisterAsync(broker, "myapp", ClientSessionContext.LocalPipe);

        Assert.Equal("myapp#1", info.ClientId);
        Assert.Null(info.Host);
        Assert.Equal(ClientTransport.Pipe, info.Transport);
        Assert.False(info.IsRemote);
        Assert.True(info.CanLaunch);

        await Cleanup(client, serve, cts);
    }

    [Fact]
    public async Task Local_And_Remote_Instances_Of_One_App_Do_Not_Collide()
    {
        // The motivating case from the design: two 'myapp' clients, one on the dev box and
        // one on the remote machine. Sharing a suffix space would hand them the same hub id, and every
        // tool call would go to whichever won the race.
        await using var broker = NewBroker();
        var local = await RegisterAsync(broker, "myapp", ClientSessionContext.LocalPipe);
        var remote = await RegisterAsync(broker, "myapp", RemoteContext);

        Assert.Equal("myapp#1", local.Info.ClientId);
        Assert.Equal("myapp@MACHINENAME#1", remote.Info.ClientId);
        Assert.Equal(2, broker.ListClients().Count);

        await Cleanup(local.Channel, local.Serve, local.Cts);
        await Cleanup(remote.Channel, remote.Serve, remote.Cts);
    }

    [Fact]
    public async Task Two_Remote_Clients_On_Different_Hosts_Are_Distinguishable()
    {
        await using var broker = NewBroker();
        var a = await RegisterAsync(broker, "myapp", RemoteContext);
        var b = await RegisterAsync(broker, "myapp", RemoteContext with { Host = "OTHERMACHINE" });

        Assert.Equal("myapp@MACHINENAME#1", a.Info.ClientId);
        Assert.Equal("myapp@OTHERMACHINE#1", b.Info.ClientId);

        await Cleanup(a.Channel, a.Serve, a.Cts);
        await Cleanup(b.Channel, b.Serve, b.Cts);
    }

    [Fact]
    public async Task Two_Remote_Clients_On_The_Same_Host_Get_Distinct_Instance_Numbers()
    {
        await using var broker = NewBroker();
        var a = await RegisterAsync(broker, "myapp", RemoteContext);
        var b = await RegisterAsync(broker, "myapp", RemoteContext);

        Assert.Equal("myapp@MACHINENAME#1", a.Info.ClientId);
        Assert.Equal("myapp@MACHINENAME#2", b.Info.ClientId);

        await Cleanup(a.Channel, a.Serve, a.Cts);
        await Cleanup(b.Channel, b.Serve, b.Cts);
    }

    [Fact]
    public async Task A_Remote_Pid_Cannot_Supersede_A_Local_Session()
    {
        // The stale-reconnect dedup matches on process id, which only means anything on THIS
        // machine. A remote client that happens to report the same pid as a running local app
        // must not evict it and steal its hub id.
        await using var broker = NewBroker();
        var local = await RegisterAsync(broker, "myapp", ClientSessionContext.LocalPipe, processId: 4242);
        var remote = await RegisterAsync(broker, "myapp", RemoteContext, processId: 4242);

        Assert.Equal("myapp#1", local.Info.ClientId);
        Assert.Equal("myapp@MACHINENAME#1", remote.Info.ClientId);
        Assert.Equal(2, broker.ListClients().Count);
        Assert.NotNull(broker.ClientStatus("myapp#1"));

        await Cleanup(local.Channel, local.Serve, local.Cts);
        await Cleanup(remote.Channel, remote.Serve, remote.Cts);
    }

    // ---------------------------------------------------------------- permissions

    [Fact]
    public async Task A_Remote_Client_Starts_ReadOnly()
    {
        await using var broker = NewBroker();
        var (client, serve, cts, info) = await RegisterAsync(broker, "myapp", RemoteContext);

        Assert.True(info.ReadOnly);
        await Cleanup(client, serve, cts);
    }

    [Fact]
    public async Task Lifting_ReadOnly_On_A_Remote_Client_Is_Remembered_For_That_Machine()
    {
        // Remote starts read-only, but the operator's decision has to stick: on a link that
        // drops as often as a mobile device's, re-authorising after every blip made the permission
        // meaningless in practice. It survives both a reconnect and a hub restart.
        var storePath = Path.Combine(Path.GetTempPath(), $"avmcp-remote-{Guid.NewGuid():N}.json");
        var options = new BrokerOptions
        {
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            WatchdogInterval = TimeSpan.FromHours(1),
        };

        await using (var broker = new PipeClientBroker(options, KnownClientStore.Open(storePath)))
        {
            var session = await RegisterAsync(broker, "myapp", RemoteContext);
            Assert.True(session.Info.ReadOnly); // first contact is always read-only

            broker.SetReadOnly(session.Info.ClientId, false);
            Assert.False(broker.ClientStatus(session.Info.ClientId)!.ReadOnly);

            // Persisted against the machine, NOT the bare app id.
            var persisted = KnownClientStore.Open(storePath);
            Assert.False(persisted.Get("myapp@MACHINENAME")!.ReadOnly);
            Assert.Equal("MACHINENAME", persisted.Get("myapp@MACHINENAME")!.Host);

            await Cleanup(session.Channel, session.Serve, session.Cts);
        }

        // A brand-new hub process still honours it.
        await using var restarted = new PipeClientBroker(options, KnownClientStore.Open(storePath));
        var reconnected = await RegisterAsync(restarted, "myapp", RemoteContext);
        Assert.False(reconnected.Info.ReadOnly);
        await Cleanup(reconnected.Channel, reconnected.Serve, reconnected.Cts);
    }

    [Fact]
    public async Task Lifting_ReadOnly_On_A_Remote_Client_Does_Not_Touch_The_Local_One()
    {
        // The reason the decision is keyed on AppId@Host rather than the bare app id. Allowing
        // yourself to drive the remote machine must not quietly make the copy of the same app running on
        // this desk writable as well -- they are different machines that happen to run the same
        // program, and the local one is the far more dangerous thing to hand over by accident.
        var storePath = Path.Combine(Path.GetTempPath(), $"avmcp-split-{Guid.NewGuid():N}.json");
        await using var broker = new PipeClientBroker(
            new BrokerOptions { WatchdogInterval = TimeSpan.FromHours(1) },
            KnownClientStore.Open(storePath));

        var local = await RegisterAsync(broker, "myapp", ClientSessionContext.LocalPipe);
        var remote = await RegisterAsync(broker, "myapp", RemoteContext);

        broker.SetReadOnly(local.Info.ClientId, true);      // lock the local one down
        broker.SetReadOnly(remote.Info.ClientId, false);    // and open the remote one

        Assert.True(broker.ClientStatus(local.Info.ClientId)!.ReadOnly);
        Assert.False(broker.ClientStatus(remote.Info.ClientId)!.ReadOnly);

        var persisted = KnownClientStore.Open(storePath);
        Assert.True(persisted.Get("myapp")!.ReadOnly);
        Assert.False(persisted.Get("myapp@MACHINENAME")!.ReadOnly);

        await Cleanup(local.Channel, local.Serve, local.Cts);
        await Cleanup(remote.Channel, remote.Serve, remote.Cts);
    }

    [Fact]
    public async Task A_Remembered_Remote_Client_Still_Cannot_Be_Launched_After_A_Hub_Restart()
    {
        // Remote registrations are persisted now (for the read-only decision), so a restored
        // entry must still be recognisable as remote. If it looked local, the seeded registry
        // entry would carry CanLaunch=true and the launch guard would wave through exactly the
        // case it exists to stop -- and "remembered but currently offline" is precisely when an
        // operator reaches for hub_launch_client.
        var storePath = Path.Combine(Path.GetTempPath(), $"avmcp-restore-{Guid.NewGuid():N}.json");
        var options = new BrokerOptions { WatchdogInterval = TimeSpan.FromHours(1) };

        await using (var broker = new PipeClientBroker(options, KnownClientStore.Open(storePath)))
        {
            var session = await RegisterAsync(broker, "myapp", RemoteContext);
            await Cleanup(session.Channel, session.Serve, session.Cts);
        }

        await using var restarted = new PipeClientBroker(options, KnownClientStore.Open(storePath));
        var known = Assert.Single(restarted.ListKnownClients());

        Assert.Equal("MACHINENAME", known.Host);
        Assert.False(known.CanLaunch);
        Assert.Equal("myapp", known.AppId);   // the bare id, not the composite key
        Assert.Null(known.ExecutablePath);        // no local path was ever recorded for it

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => restarted.LaunchClientAsync(known.ClientId));
        Assert.Contains("cannot start or stop processes", ex.Message);
    }

    [Fact]
    public async Task A_Local_Client_Still_Persists_Its_ReadOnly_Setting()
    {
        // The existing behaviour must be untouched.
        var storePath = Path.Combine(Path.GetTempPath(), $"avmcp-local-{Guid.NewGuid():N}.json");
        var options = new BrokerOptions { WatchdogInterval = TimeSpan.FromHours(1) };

        await using var broker = new PipeClientBroker(options, KnownClientStore.Open(storePath));
        var session = await RegisterAsync(broker, "localapp", ClientSessionContext.LocalPipe);
        broker.SetReadOnly(session.Info.ClientId, true);

        Assert.True(KnownClientStore.Open(storePath).Get("localapp")!.ReadOnly);
        await Cleanup(session.Channel, session.Serve, session.Cts);
    }

    // ---------------------------------------------------------------- activation

    [Fact]
    public async Task A_Remote_Client_Never_Auto_Activates()
    {
        // Tool calls go to whichever client is active. If a remote client auto-activated on
        // connect, then whatever attached first would receive the operator's calls -- their
        // arguments included -- and answer them.
        await using var broker = NewBroker();
        var (client, serve, cts, info) = await RegisterAsync(broker, "myapp", RemoteContext);

        Assert.Null(broker.ActiveClientId);

        // It becomes active only when explicitly selected.
        broker.ActiveClientId = info.ClientId;
        Assert.Equal(info.ClientId, broker.ActiveClientId);

        await Cleanup(client, serve, cts);
    }

    [Fact]
    public async Task A_Remote_Client_Cannot_Steal_Active_From_A_Local_One()
    {
        await using var broker = NewBroker();
        var local = await RegisterAsync(broker, "localapp", ClientSessionContext.LocalPipe);
        Assert.Equal(local.Info.ClientId, broker.ActiveClientId);

        var remote = await RegisterAsync(broker, "myapp", RemoteContext);
        Assert.Equal(local.Info.ClientId, broker.ActiveClientId);

        await Cleanup(local.Channel, local.Serve, local.Cts);
        await Cleanup(remote.Channel, remote.Serve, remote.Cts);
    }

    [Fact]
    public async Task A_Local_Client_Still_Auto_Activates()
    {
        await using var broker = NewBroker();
        var (client, serve, cts, info) = await RegisterAsync(broker, "localapp", ClientSessionContext.LocalPipe);

        Assert.Equal(info.ClientId, broker.ActiveClientId);
        await Cleanup(client, serve, cts);
    }

    // ---------------------------------------------------------------- launch safety

    [Fact]
    public async Task Launching_A_Remote_Client_Is_Refused_And_Says_Where_It_Lives()
    {
        await using var broker = NewBroker();
        var (client, serve, cts, info) = await RegisterAsync(broker, "myapp", RemoteContext);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.LaunchClientAsync(info.ClientId));
        Assert.Contains("MACHINENAME", ex.Message);
        Assert.Contains("remote", ex.Message, StringComparison.OrdinalIgnoreCase);

        await Cleanup(client, serve, cts);
    }

    [Fact]
    public async Task Restarting_A_Remote_Client_Is_Refused()
    {
        await using var broker = NewBroker();
        var (client, serve, cts, info) = await RegisterAsync(broker, "myapp", RemoteContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.RestartClientAsync(info.ClientId));
        await Cleanup(client, serve, cts);
    }

    [Fact]
    public async Task Restarting_A_Remote_Client_Never_Starts_A_LOCAL_Copy_Instead()
    {
        // The worst failure this design can have, and the reason CanLaunch is a stored fact
        // rather than something re-derived from the id. A LOCAL 'myapp' has a recorded
        // executable path; 'myapp@MACHINENAME#1' strips to 'myapp@MACHINENAME', misses,
        // and could otherwise fall back to that local profile -- starting a local app, and
        // returning a process id, while the operator believes they restarted the remote machine.
        var storePath = Path.Combine(Path.GetTempPath(), $"avmcp-both-{Guid.NewGuid():N}.json");
        var store = KnownClientStore.Open(storePath);
        store.Upsert(new KnownClientProfile
        {
            AppId = "myapp",
            DisplayName = "myapp",
            // A real path -- if the guard were missing, this WOULD launch.
            ExecutablePath = Environment.ProcessPath ?? "C:\\Windows\\System32\\cmd.exe",
            LastSeenUtc = DateTimeOffset.UtcNow,
        });

        await using var broker = new PipeClientBroker(
            new BrokerOptions { WatchdogInterval = TimeSpan.FromHours(1) }, store);
        var (client, serve, cts, info) = await RegisterAsync(broker, "myapp", RemoteContext);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.RestartClientAsync(info.ClientId));
        Assert.Contains("cannot start or stop processes", ex.Message);

        await Cleanup(client, serve, cts);
    }

    [Fact]
    public async Task A_Remote_Registration_Writes_No_Launch_Profile()
    {
        // Because a profile is keyed by bare app id and records a LOCAL path, writing one for
        // a remote client would make a later hub_launch_client for that app id start a local
        // copy of something that only ever ran elsewhere.
        var storePath = Path.Combine(Path.GetTempPath(), $"avmcp-noprofile-{Guid.NewGuid():N}.json");
        await using var broker = new PipeClientBroker(
            new BrokerOptions { WatchdogInterval = TimeSpan.FromHours(1) }, KnownClientStore.Open(storePath));

        var (client, serve, cts, _) = await RegisterAsync(broker, "myapp", RemoteContext);

        Assert.Null(KnownClientStore.Open(storePath).Get("myapp"));
        await Cleanup(client, serve, cts);
    }

    // ---------------------------------------------------------------- discovery

    [Fact]
    public async Task WaitForClient_Matches_A_Remote_Client_By_App_Id_Or_App_At_Host()
    {
        await using var broker = NewBroker();
        var (client, serve, cts, info) = await RegisterAsync(broker, "myapp", RemoteContext);
        var budget = TimeSpan.FromSeconds(5);

        Assert.Equal(info.ClientId, (await broker.WaitForClientAsync("myapp", budget))!.ClientId);
        Assert.Equal(info.ClientId, (await broker.WaitForClientAsync("myapp@MACHINENAME", budget))!.ClientId);
        Assert.Equal(info.ClientId, (await broker.WaitForClientAsync(info.ClientId, budget))!.ClientId);
        Assert.Null(await broker.WaitForClientAsync("myapp@OTHERMACHINE", TimeSpan.FromMilliseconds(200)));

        await Cleanup(client, serve, cts);
    }

    [Fact]
    public async Task The_AppAtHost_Filter_Disambiguates_A_Local_From_A_Remote_Instance()
    {
        await using var broker = NewBroker();
        var local = await RegisterAsync(broker, "myapp", ClientSessionContext.LocalPipe);
        var remote = await RegisterAsync(broker, "myapp", RemoteContext);
        var budget = TimeSpan.FromSeconds(5);

        // The bare app id still matches SOMETHING -- which of the two is a coin toss, and that
        // is exactly why the app@host form exists.
        Assert.NotNull(await broker.WaitForClientAsync("myapp", budget));
        Assert.Equal(remote.Info.ClientId,
            (await broker.WaitForClientAsync("myapp@MACHINENAME", budget))!.ClientId);

        await Cleanup(local.Channel, local.Serve, local.Cts);
        await Cleanup(remote.Channel, remote.Serve, remote.Cts);
    }

    // ---------------------------------------------------------------- enrollment

    [Fact]
    public async Task Enrollment_Is_Refused_Over_A_Remote_Transport()
    {
        // The second, independent gate. The remote handshake already refuses this message
        // kind before a session can reach the broker; this proves the broker refuses it too,
        // so the property does not depend on one check in one place staying correct.
        await using var broker = NewBroker();
        broker.CredentialIssuer = new AlwaysIssues();

        var (client, brokerSide) = DuplexPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serve = broker.AcceptChannel(brokerSide, RemoteContext, cts.Token);

        await client.SendAsync(MessageKind.EnrollRequest, new EnrollRequestMessage { TargetName = "attacker" });

        var envelope = await client.ReceiveAsync(cts.Token);
        Assert.Equal(MessageKind.Rejected, envelope!.Kind);
        Assert.Equal(RejectReason.NotPermittedOnTransport, envelope.Unwrap<RejectedMessage>()!.Code);

        await client.DisposeAsync();
        try { await serve; } catch { }
    }

    [Fact]
    public async Task Enrollment_Over_The_Local_Pipe_Reaches_The_Issuer()
    {
        await using var broker = NewBroker();
        broker.CredentialIssuer = new AlwaysIssues();

        var (client, brokerSide) = DuplexPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serve = broker.AcceptChannel(brokerSide, ClientSessionContext.LocalPipe, cts.Token);

        await client.SendAsync(MessageKind.EnrollRequest, new EnrollRequestMessage { TargetName = "remotehost" });

        var envelope = await client.ReceiveAsync(cts.Token);
        Assert.Equal(MessageKind.EnrollResponse, envelope!.Kind);
        Assert.True(envelope.Unwrap<EnrollResponseMessage>()!.Accepted);

        await client.DisposeAsync();
        try { await serve; } catch { }
    }

    [Fact]
    public async Task A_Hub_Without_Remote_Support_Refuses_Enrollment_Politely()
    {
        await using var broker = NewBroker(); // no issuer installed

        var (client, brokerSide) = DuplexPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serve = broker.AcceptChannel(brokerSide, ClientSessionContext.LocalPipe, cts.Token);

        await client.SendAsync(MessageKind.EnrollRequest, new EnrollRequestMessage { TargetName = "remotehost" });

        var response = (await client.ReceiveAsync(cts.Token))!.Unwrap<EnrollResponseMessage>()!;
        Assert.False(response.Accepted);
        Assert.Contains("remote", response.Reason!, StringComparison.OrdinalIgnoreCase);

        await client.DisposeAsync();
        try { await serve; } catch { }
    }

    // ---------------------------------------------------------------- helpers

    private sealed class AlwaysIssues : Keincheck.Hub.Remote.ICredentialIssuer
    {
        public EnrollResponseMessage Issue(EnrollRequestMessage request) =>
            new() { Accepted = true, Bundle = "kcrem1_stub", Serial = "AA", NotAfter = DateTimeOffset.UtcNow.AddDays(1) };
    }

    private static async Task Cleanup(PipeChannel client, Task serve, CancellationTokenSource cts)
    {
        cts.Cancel();
        await client.DisposeAsync();
        try { await serve; } catch { /* cancellation */ }
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
