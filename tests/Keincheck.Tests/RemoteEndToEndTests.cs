using System.Text.Json;
using Keincheck.Hub;
using Keincheck.Hub.Remote;
using Keincheck.Protocol;
using Keincheck.Remote;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// The whole remote path, end to end, with nothing stubbed: a real certificate authority, a
/// real TLS listener on loopback, a real client dialling in with a hub-issued credential, and
/// the real broker registry on the other side.
/// </summary>
/// <remarks>
/// This is the test that would have caught the EKU flaw, the launch-a-local-copy flaw, and the
/// read-only-by-default regression — each of which looks fine in isolation and only misbehaves
/// once the pieces are wired together.
/// </remarks>
public sealed class RemoteEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kc-e2e-{Guid.NewGuid():N}");

    private sealed record Rig(
        PipeClientBroker Broker, RemoteAccess Remote, HubAuditLog Audit, RemoteEndpoint Endpoint) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Remote.DisposeAsync();
            await Broker.DisposeAsync();
        }
    }

    /// <summary>Stands up a hub with remote access enabled on an OS-chosen loopback port.</summary>
    private async Task<Rig> StartHubAsync()
    {
        var audit = new HubAuditLog();
        var broker = new PipeClientBroker(
            new BrokerOptions
            {
                WatchdogInterval = TimeSpan.FromHours(1),
                HeartbeatTimeout = TimeSpan.FromMinutes(5),
                InvokeTimeout = TimeSpan.FromSeconds(5),
                // A unique pipe name per test: the broker's own pipe accept-loop is not started
                // here, but the name must not collide with a real hub on the machine.
                PipeName = $"Keincheck.test.{Guid.NewGuid():N}",
            },
            KnownClientStore.Open(Path.Combine(_dir, "known-clients.json")),
            audit);

        var store = RemoteStore.Open(Path.Combine(_dir, "remote"));
        var remote = new RemoteAccess(broker, audit, store);
        await remote.EnableAsync(new RemoteSettings { Enabled = true, BindAddress = "127.0.0.1", Port = 0 });

        Assert.True(remote.IsListening, "the listener must be up for this test to mean anything");
        var bound = RemoteEndpoint.Parse(remote.BoundEndpoint!);
        await Task.Yield();
        return new Rig(broker, remote, audit, bound);
    }

    /// <summary>A connected remote client that has registered and published a tool catalog.</summary>
    private sealed record Client(PipeChannel Channel, Task Pump, CancellationTokenSource Cts) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Channel.DisposeAsync();
            try { await Pump; } catch { /* cancellation */ }
            Cts.Dispose();
        }
    }

    /// <summary>
    /// Dials the hub, completes the handshake, registers, and starts answering InvokeTool.
    /// </summary>
    private static async Task<Client> ConnectAsync(
        Rig rig, RemoteCredential credential, string appId, IReadOnlyList<ToolDescriptor>? tools = null)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var channel = await StreamTransport.ConnectAsync(rig.Endpoint, credential, TimeSpan.FromSeconds(10), cts.Token);

        await RemoteHandshake.ClientAsync(channel, "test-client", cts.Token);
        await channel.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = appId,
            DisplayName = appId,
            ProcessId = Environment.ProcessId,
            ProtocolVersion = ProtocolVersion.Current,
        }, cancellationToken: cts.Token);
        await channel.SendAsync(MessageKind.ToolList, new ToolListMessage
        {
            ClientId = appId,
            Tools = tools ??
            [
                new ToolDescriptor { Name = "get_logical_tree", ReadOnly = true },
                new ToolDescriptor { Name = "describe_screen", ReadOnly = true },
                new ToolDescriptor { Name = "click_at", ReadOnly = false },
            ],
        }, cancellationToken: cts.Token);

        // Answer tool calls so invocations complete rather than timing out.
        var pump = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var envelope = await channel.ReceiveAsync(cts.Token);
                if (envelope is null) break;
                if (envelope.Kind != MessageKind.InvokeTool) continue;

                var invoke = envelope.Unwrap<InvokeToolMessage>()!;
                await channel.SendAsync(MessageKind.ToolResult, new ToolResultMessage
                {
                    ClientId = appId,
                    ToolName = invoke.ToolName,
                    Content = JsonSerializer.SerializeToElement(new[]
                    {
                        new { type = "text", text = $"ran {invoke.ToolName}" },
                    }),
                }, envelope.CorrelationId, cts.Token);
            }
        }, cts.Token);

        return new Client(channel, pump, cts);
    }

    private static async Task<ClientInfo> WaitForClientAsync(PipeClientBroker broker, string appAtHost)
    {
        var info = await broker.WaitForClientAsync(appAtHost, TimeSpan.FromSeconds(15));
        Assert.NotNull(info);

        // ...and for the tool catalog to land, which arrives just after registration.
        for (var i = 0; i < 250 && broker.ClientStatus(info!.ClientId)?.Tools.Count is null or 0; i++)
            await Task.Delay(20);
        return broker.ClientStatus(info!.ClientId)!;
    }

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task A_Remote_App_Attaches_And_Is_Driven_Through_The_Same_Tools_As_A_Local_One()
    {
        await using var rig = await StartHubAsync();
        var (bundle, record) = rig.Remote.Issue("OP3R4T0RV2");
        using var credential = RemoteCredential.Parse(bundle);
        await using var client = await ConnectAsync(rig, credential, "protoface");

        var info = await WaitForClientAsync(rig.Broker, "protoface@OP3R4T0RV2");

        // The identity is derived from the certificate, so it is trustworthy.
        Assert.Equal("protoface@OP3R4T0RV2#1", info.ClientId);
        Assert.Equal("OP3R4T0RV2", info.Host);
        Assert.Equal(ClientTransport.Tcp, info.Transport);
        Assert.Equal(record.Host, info.Host);

        // Read-only by default, and NOT active until someone chooses it.
        Assert.True(info.ReadOnly);
        Assert.False(info.CanLaunch);
        Assert.Null(rig.Broker.ActiveClientId);

        // Looking works...
        var result = await rig.Broker.InvokeOnClientAsync(info.ClientId, "get_logical_tree", null);
        Assert.False(result.IsError);

        // ...including the tools the old name-based gate wrongly refused.
        Assert.False((await rig.Broker.InvokeOnClientAsync(info.ClientId, "describe_screen", null)).IsError);

        // ...but driving does not, until it is deliberately allowed.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Broker.InvokeOnClientAsync(info.ClientId, "click_at", null));
        Assert.Contains("read-only", refused.Message);

        rig.Broker.SetReadOnly(info.ClientId, false);
        Assert.False((await rig.Broker.InvokeOnClientAsync(info.ClientId, "click_at", null)).IsError);
    }

    [Fact]
    public async Task The_Audit_Trail_Records_The_Attachment_And_Reaches_Disk()
    {
        await using var rig = await StartHubAsync();
        var (bundle, _) = rig.Remote.Issue("OP3R4T0RV2");
        using var credential = RemoteCredential.Parse(bundle);
        await using var client = await ConnectAsync(rig, credential, "protoface");
        await WaitForClientAsync(rig.Broker, "protoface@OP3R4T0RV2");

        var entries = rig.Audit.Snapshot();
        Assert.Contains(entries, e => e.Kind == AuditKind.Enroll && e.Host == "OP3R4T0RV2");
        Assert.Contains(entries, e => e.Kind == AuditKind.Attach && e.Host == "OP3R4T0RV2");
        Assert.Contains(entries, e => e.Kind == AuditKind.RemoteToggled);

        // Enabling remote installs the durable sink -- a 500-entry ring is not an audit trail
        // for a machine nobody is watching.
        Assert.NotNull(rig.Audit.Sink);
    }

    // ---------------------------------------------------------------- adversarial

    [Fact]
    public async Task A_Credential_From_A_Different_Hub_Cannot_Attach()
    {
        await using var rig = await StartHubAsync();

        // Someone else's hub, with its own CA. Same host label, so only the signature differs.
        using var otherStore = RemoteStore.Open(Path.Combine(_dir, "other-hub"));
        otherStore.Provision();
        var (foreignBundle, _) = otherStore.Issue("OP3R4T0RV2", TimeSpan.FromDays(1), "test");
        using var foreign = RemoteCredential.Parse(foreignBundle);

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(rig, foreign, "protoface"));
        Assert.Empty(rig.Broker.ListClients());
    }

    [Fact]
    public async Task A_Revoked_Credential_Is_Refused_With_A_Reason()
    {
        await using var rig = await StartHubAsync();
        var (bundle, record) = rig.Remote.Issue("OP3R4T0RV2");
        using var credential = RemoteCredential.Parse(bundle);

        // It works first...
        await using (var _ = await ConnectAsync(rig, credential, "protoface"))
            await WaitForClientAsync(rig.Broker, "protoface@OP3R4T0RV2");

        Assert.True(rig.Remote.Revoke(record.Serial));

        // ...and then does not. The certificate is still perfectly valid and correctly signed;
        // it is the hub's own list that refuses it, which is what makes a leaked build
        // credential recoverable rather than permanent.
        var ex = await Assert.ThrowsAsync<RemoteHandshake.RejectedException>(
            () => ConnectAsync(rig, credential, "protoface"));
        Assert.Equal(RejectReason.Revoked, ex.Code);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task Disabling_Remote_Access_Stops_New_Connections()
    {
        await using var rig = await StartHubAsync();
        var (bundle, _) = rig.Remote.Issue("OP3R4T0RV2");
        using var credential = RemoteCredential.Parse(bundle);

        await rig.Remote.DisableAsync();
        Assert.False(rig.Remote.IsListening);

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(rig, credential, "protoface"));
    }

    [Fact]
    public async Task Enrollment_Over_The_Remote_Transport_Is_Refused()
    {
        // Belt and braces against the worst escalation: a remote peer minting more credentials
        // would turn one leaked build certificate into an unbounded, self-renewing grant.
        await using var rig = await StartHubAsync();
        var (bundle, _) = rig.Remote.Issue("OP3R4T0RV2");
        using var credential = RemoteCredential.Parse(bundle);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var channel = await StreamTransport.ConnectAsync(
            rig.Endpoint, credential, TimeSpan.FromSeconds(10), cts.Token);

        await channel.SendAsync(MessageKind.EnrollRequest, new EnrollRequestMessage
        {
            TargetName = "attacker", RequestedDays = 3650,
        }, cancellationToken: cts.Token);

        var envelope = await channel.ReceiveAsync(cts.Token);
        Assert.Equal(MessageKind.Rejected, envelope!.Kind);
        Assert.Equal(RejectReason.NotPermittedOnTransport, envelope.Unwrap<RejectedMessage>()!.Code);

        // No extra credential was minted.
        Assert.Single(rig.Remote.Store.Issued());
    }

    [Fact]
    public async Task An_Oversized_PreAuth_Frame_Is_Refused_And_The_Hub_Keeps_Serving()
    {
        // The listener clamps framing until a session is accepted, so an authenticated-but-not-
        // yet-welcomed peer cannot drive a 32 MiB reassembly. The important half of the
        // assertion is the second one: the hub is still fine afterwards.
        await using var rig = await StartHubAsync();
        var (bundle, _) = rig.Remote.Issue("OP3R4T0RV2");
        using var credential = RemoteCredential.Parse(bundle);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var hostile = await StreamTransport.ConnectAsync(
            rig.Endpoint, credential, TimeSpan.FromSeconds(10), cts.Token))
        {
            // Client-side limits are ours to set; a real attacker would simply not clamp.
            hostile.Limits = ChannelLimits.Default;

            // Both halves can throw: the hub tears the connection down as soon as it sees an
            // over-large chunk header, so the rest of the write hits a reset socket. Either
            // way the peer is refused, which is the thing being asserted.
            try
            {
                await hostile.SendAsync(MessageKind.Hello, new HelloMessage
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    MachineName = new string('x', 256 * 1024),
                }, cancellationToken: cts.Token);

                await hostile.ReceiveAsync(cts.Token);
            }
            catch
            {
                // expected
            }
        }

        // The point of the test: the hub is unharmed and still serving.
        await using var good = await ConnectAsync(rig, credential, "protoface");
        var info = await WaitForClientAsync(rig.Broker, "protoface@OP3R4T0RV2");
        Assert.Equal("OP3R4T0RV2", info.Host);
        Assert.Contains(rig.Audit.Snapshot(), e => e.Kind == AuditKind.AuthFailure);
    }

    [Fact]
    public async Task More_Clients_Than_The_Pending_Handshake_Cap_Can_Be_Connected_At_Once()
    {
        // The pending-session cap bounds peers that are mid-handshake, i.e. not yet known to
        // be anyone. It must NOT bound established clients: holding a slot for the life of a
        // session would silently cap the hub at MaxPendingSessions clients and refuse every
        // connection after that, with no error anywhere except the refused peer.
        await using var rig = await StartHubAsync();
        var clients = new List<Client>();
        var credentials = new List<RemoteCredential>();
        try
        {
            var count = RemoteClientListener.MaxPendingSessions + 3;
            for (var i = 0; i < count; i++)
            {
                var (bundle, _) = rig.Remote.Issue($"host-{i:00}");
                var credential = RemoteCredential.Parse(bundle);
                credentials.Add(credential);
                clients.Add(await ConnectAsync(rig, credential, "protoface"));
                await WaitForClientAsync(rig.Broker, $"protoface@host-{i:00}");
            }

            Assert.Equal(count, rig.Broker.ListClients().Count);
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
            foreach (var credential in credentials) credential.Dispose();
        }
    }

    [Fact]
    public async Task The_Hub_Actually_Sends_The_Heartbeats_It_Promised()
    {
        // Regression test for a bug every other test missed because they all finished in
        // under a second. The hub advertised the bidirectional-heartbeat capability, the
        // client set its idle-read deadline from it -- and the hub never sent one. Remote
        // sessions therefore died the moment they went quiet between tool calls, which is
        // most of the time, and reconnected forever. It only showed up once a client was
        // left running on a real machine.
        await using var rig = await StartHubAsync();
        var (bundle, _) = rig.Remote.Issue("OP3R4T0RV2");
        using var credential = RemoteCredential.Parse(bundle);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var channel = await StreamTransport.ConnectAsync(
            rig.Endpoint, credential, TimeSpan.FromSeconds(10), cts.Token);

        var result = await RemoteHandshake.ClientAsync(channel, "test", cts.Token);

        // The hub must state an interval, not leave the client to guess one.
        Assert.True(result.Heartbeat, "the capability should have been agreed");
        Assert.NotNull(result.ServerHeartbeat);
        Assert.True(result.ServerHeartbeat!.Value > TimeSpan.Zero);

        await channel.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = "protoface", ProtocolVersion = ProtocolVersion.Current,
        }, cancellationToken: cts.Token);

        // Sit idle -- exactly what a real client does between tool calls -- and require the
        // hub to speak first, twice, unprompted.
        var deadline = result.ServerHeartbeat.Value * 4;
        for (var i = 0; i < 2; i++)
        {
            using var beat = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            beat.CancelAfter(deadline);

            MessageEnvelope? envelope;
            try
            {
                envelope = await channel.ReceiveAsync(beat.Token);
            }
            catch (OperationCanceledException) when (beat.IsCancellationRequested)
            {
                Assert.Fail(
                    $"The hub promised a heartbeat every {result.ServerHeartbeat} but sent none " +
                    $"within {deadline}. A real client would tear the session down here.");
                return;
            }

            Assert.NotNull(envelope);
            Assert.Equal(MessageKind.Heartbeat, envelope!.Kind);
        }

        // And the session is still healthy afterwards, not merely alive.
        Assert.Single(rig.Broker.ListClients());
    }

    [Fact]
    public async Task A_Client_Applies_An_Idle_Deadline_Only_When_The_Hub_Promised_Heartbeats()
    {
        // The connector must derive its deadline from the hub's promise. Inventing one the hub
        // was never going to satisfy is a self-inflicted disconnect loop that looks exactly
        // like a flaky network -- the hardest kind of bug to attribute.
        await using var rig = await StartHubAsync();
        var (bundle, _) = rig.Remote.Issue("OP3R4T0RV2");
        using var credential = RemoteCredential.Parse(bundle);

        using var connector = new RemoteChannelConnector(credential, rig.Endpoint, ownsCredential: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var session = await connector.ConnectAsync(
            new ChannelConnectContext { AppId = "protoface", HeartbeatInterval = TimeSpan.FromSeconds(5) },
            cts.Token);

        await using (session.Channel)
        {
            Assert.NotNull(session.ReadTimeout);
            // Comfortably longer than the hub's own interval, so a lost packet or two does not
            // tear down a healthy link.
            Assert.True(session.ReadTimeout!.Value >= RemoteHandshake.ServerHeartbeatInterval * 3,
                $"deadline {session.ReadTimeout} leaves too little margin over the hub's " +
                $"{RemoteHandshake.ServerHeartbeatInterval} heartbeat");
        }
    }

    [Fact]
    public async Task A_Host_Label_Cannot_Smuggle_An_Instance_Suffix_Into_The_Hub_Id()
    {
        // Host labels are validated at ISSUE time, which is what keeps AppId@Host#n parseable.
        await using var rig = await StartHubAsync();

        Assert.ThrowsAny<ArgumentException>(() => rig.Remote.Issue("evil#9"));
        Assert.ThrowsAny<ArgumentException>(() => rig.Remote.Issue("evil@elsewhere"));
        Assert.ThrowsAny<ArgumentException>(() => rig.Remote.Issue("has space"));
    }

    [Fact]
    public async Task Screenshot_Sized_Payloads_Survive_The_Remote_Path()
    {
        // Screenshots are base64-in-JSON and are the largest thing that crosses this link, so
        // an accepted session must carry multi-megabyte results -- including through the
        // Brotli compression the handshake negotiates.
        await using var rig = await StartHubAsync();
        var (bundle, _) = rig.Remote.Issue("OP3R4T0RV2");
        using var credential = RemoteCredential.Parse(bundle);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var channel = await StreamTransport.ConnectAsync(rig.Endpoint, credential, TimeSpan.FromSeconds(10), cts.Token);
        var handshake = await RemoteHandshake.ClientAsync(channel, "test", cts.Token);
        Assert.True(handshake.Compression, "compression should have been negotiated");

        await channel.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = "protoface", ProtocolVersion = ProtocolVersion.Current,
        }, cancellationToken: cts.Token);
        await channel.SendAsync(MessageKind.ToolList, new ToolListMessage
        {
            ClientId = "protoface",
            Tools = [new ToolDescriptor { Name = "screenshot_window", ReadOnly = true }],
        }, cancellationToken: cts.Token);

        var big = new string('A', 4 * 1024 * 1024); // ~4 MiB, a plausible base64 PNG
        var pump = Task.Run(async () =>
        {
            var envelope = await channel.ReceiveAsync(cts.Token);
            var invoke = envelope!.Unwrap<InvokeToolMessage>()!;
            await channel.SendAsync(MessageKind.ToolResult, new ToolResultMessage
            {
                ClientId = "protoface",
                ToolName = invoke.ToolName,
                Content = JsonSerializer.SerializeToElement(new[]
                {
                    new { type = "text", text = big },
                }),
            }, envelope.CorrelationId, cts.Token);
        }, cts.Token);

        try
        {
            var info = await WaitForClientAsync(rig.Broker, "protoface@OP3R4T0RV2");
            var result = await rig.Broker.InvokeOnClientAsync(info.ClientId, "screenshot_window", null);

            Assert.False(result.IsError);
            Assert.Equal(big, result.Content!.Value[0].GetProperty("text").GetString());
        }
        finally
        {
            cts.Cancel();
            await channel.DisposeAsync();
            try { await pump; } catch { }
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
