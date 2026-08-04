using System.IO.Pipelines;
using Keincheck.Hub;
using Keincheck.Protocol;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// A remote session is only watched once it registers — the heartbeat watchdog scans the
/// registry, and an unregistered session is not in it. These cover the window in between.
/// </summary>
public sealed class RemoteSessionReapingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(400);

    private static ClientSessionContext Remote(string host = "OP3R4T0RV2") => new()
    {
        Transport = ClientTransport.Tcp,
        Host = host,
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

    private static PipeClientBroker NewBroker(TimeSpan? registrationTimeout = null) => new(
        new BrokerOptions
        {
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            WatchdogInterval = TimeSpan.FromHours(1),   // the watchdog must NOT be what reaps these
            InvokeTimeout = TimeSpan.FromSeconds(2),
            RegistrationTimeout = registrationTimeout ?? Deadline,
        },
        KnownClientStore.Open(Path.Combine(Path.GetTempPath(), $"avmcp-reap-{Guid.NewGuid():N}.json")));

    [Fact]
    public async Task A_Remote_Session_That_Never_Registers_Is_Dropped()
    {
        // The wedge is reachable by SILENCE — no malformed frame, no protocol trick. A peer that
        // holds a valid certificate completes the handshake and then simply says nothing. Before
        // the deadline this parked in ReceiveAsync for the hub's lifetime, holding a socket, an
        // SslStream, the session task and a heartbeat pump still writing every few seconds.
        // The watchdog cannot save it: it scans the registry, and this session never got there.
        await using var broker = NewBroker();
        var (client, brokerSide) = DuplexPair();

        var serve = broker.AcceptChannel(brokerSide, Remote(), CancellationToken.None);

        // Say nothing at all — just hold the connection open.
        await serve.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(serve.IsCompletedSuccessfully);
        Assert.Empty(broker.ListClients());
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dropping_An_Unregistered_Session_Is_Audited_As_An_AuthFailure()
    {
        // A silent reap would be worse than the leak in one respect: an Attach entry with no
        // matching Detach is the only trace, and nothing says why.
        await using var broker = NewBroker();
        var (client, brokerSide) = DuplexPair();

        await broker.AcceptChannel(brokerSide, Remote("GHOSTBOX"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var entry = Assert.Single(broker.Audit.Snapshot(), e => e.Kind == AuditKind.AuthFailure);
        Assert.Equal("GHOSTBOX", entry.Host);
        Assert.Equal(ClientTransport.Tcp, entry.Transport);
        Assert.Contains("Register", entry.Error);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Registering_Inside_The_Window_Retires_The_Deadline()
    {
        // The deadline covers the unregistered phase only. Once a client is in the registry the
        // watchdog owns liveness, so a long-lived session must not be killed at the deadline —
        // which is what would happen if the receive loop kept reading against that token.
        await using var broker = NewBroker();
        var connected = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.ClientConnected += (_, i) => connected.TrySetResult(i);

        var (client, brokerSide) = DuplexPair();
        var serve = broker.AcceptChannel(brokerSide, Remote(), CancellationToken.None);

        await client.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = "protoface",
            DisplayName = "protoface",
            ProtocolVersion = ProtocolVersion.Current,
        });
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Outlive the deadline by a comfortable margin, then prove the session still works.
        await Task.Delay(Deadline + Deadline + TimeSpan.FromMilliseconds(300));

        Assert.False(serve.IsCompleted, "the registration deadline was still armed after Register.");
        Assert.Single(broker.ListClients());

        await client.SendAsync(MessageKind.Heartbeat, new HeartbeatMessage());
        await client.DisposeAsync();
        await serve.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_Local_Pipe_Session_Is_Exempt()
    {
        // The control pipe is CurrentUserOnly: a peer that can wedge a session there can already
        // do strictly worse things directly, and v1 clients are free to connect and stay quiet
        // before registering. Applying a network defence to it would break them for no gain.
        await using var broker = NewBroker();
        var (client, brokerSide) = DuplexPair();

        var serve = broker.AcceptChannel(brokerSide, ClientSessionContext.LocalPipe, CancellationToken.None);
        await Task.Delay(Deadline + Deadline);

        Assert.False(serve.IsCompleted, "a local pipe session was reaped by the registration deadline.");

        await client.DisposeAsync();
        await serve.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_Enrollment_Session_Is_Not_Reaped_Before_It_Is_Answered()
    {
        // An enrollment session legitimately never registers: it asks one question, gets one
        // answer and closes. It must complete on its own terms rather than being cut off.
        await using var broker = NewBroker(TimeSpan.FromSeconds(30));
        var (client, brokerSide) = DuplexPair();

        var serve = broker.AcceptChannel(brokerSide, ClientSessionContext.LocalPipe, CancellationToken.None);
        await client.SendAsync(MessageKind.EnrollRequest, new EnrollRequestMessage { TargetName = "ci" });

        var reply = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(MessageKind.EnrollResponse, reply!.Kind);

        await serve.WaitAsync(TimeSpan.FromSeconds(10));
        await client.DisposeAsync();
    }

    /// <summary>Joins one pipe's reader to another's writer so two channels can talk in-memory.</summary>
    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => read.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => write.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
