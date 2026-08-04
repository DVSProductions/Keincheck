using System.IO.Pipelines;
using Keincheck.Protocol;
using Keincheck.Remote;
using Xunit;

namespace Keincheck.Remote.Tests;

/// <summary>
/// Tests for the post-TLS Hello / Welcome / Rejected exchange, driven over in-memory streams.
/// TLS itself is covered separately; what matters here is the state machine — especially that
/// every refusal is <i>told</i> to the peer instead of surfacing as a bare disconnect.
/// </summary>
public sealed class RemoteHandshakeTests
{
    private static (PipeChannel Client, PipeChannel Server) DuplexPair(ChannelLimits? serverLimits = null)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        return (
            new PipeChannel(new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream())),
            new PipeChannel(new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
                ownsStream: true, serverLimits ?? ChannelLimits.Handshake));
    }

    [Fact]
    public async Task A_Successful_Handshake_Agrees_On_Host_And_Capabilities()
    {
        var (client, server) = DuplexPair();
        await using (client)
        await using (server)
        {
            var serverTask = RemoteHandshake.ServerAsync(server, "MACHINENAME", "9.9.9");
            var clientResult = await RemoteHandshake.ClientAsync(client, "1.2.3");
            var serverResult = await serverTask;

            Assert.NotNull(serverResult);
            Assert.Equal("MACHINENAME", clientResult.Host);
            Assert.Equal("9.9.9", clientResult.ServerVersion);
            Assert.True(clientResult.Compression);
            Assert.Contains(RemoteCapabilities.Brotli, clientResult.Capabilities);
        }
    }

    [Fact]
    public async Task Accepting_The_Session_Is_What_Widens_The_Framing_Limits()
    {
        // Before Welcome the peer is authenticated but not yet accepted, so it gets the small
        // handshake clamp. This is the moment that changes -- and it must be the ONLY moment.
        var (client, server) = DuplexPair();
        await using (client)
        await using (server)
        {
            Assert.Equal(ChannelLimits.Handshake.MaxMessageSize, server.Limits.MaxMessageSize);

            var serverTask = RemoteHandshake.ServerAsync(server, "remotehost", "9.9.9");
            await RemoteHandshake.ClientAsync(client, "1.2.3");
            await serverTask;

            Assert.Equal(ChannelLimits.Default.MaxMessageSize, server.Limits.MaxMessageSize);
            Assert.True(server.Limits.AllowCompression);
            Assert.True(client.Limits.AllowCompression);
        }
    }

    [Fact]
    public async Task Compression_Stays_Off_When_The_Peer_Does_Not_Offer_It()
    {
        // Capability intersection, not assumption: an older peer must never be sent a frame
        // it cannot inflate.
        var (client, server) = DuplexPair();
        await using (client)
        await using (server)
        {
            var serverTask = RemoteHandshake.ServerAsync(server, "remotehost", "9.9.9");

            await client.SendAsync(MessageKind.Hello, new HelloMessage
            {
                ProtocolVersion = ProtocolVersion.Current,
                MachineName = "old-client",
                Capabilities = [],           // a peer that advertises nothing
            });

            var result = await serverTask;
            Assert.NotNull(result);
            Assert.False(result!.Compression);
            Assert.False(server.Limits.AllowCompression);
        }
    }

    [Fact]
    public async Task An_Unsupported_Protocol_Version_Is_Refused_With_A_Reason()
    {
        // The whole point of Rejected. On the local pipe a version mismatch is a silent close
        // -- survivable at your desk, close to undebuggable against a headless machine.
        var (client, server) = DuplexPair();
        await using (client)
        await using (server)
        {
            var serverTask = RemoteHandshake.ServerAsync(server, "remotehost", "9.9.9");
            await client.SendAsync(MessageKind.Hello, new HelloMessage { ProtocolVersion = 9999 });

            Assert.Null(await serverTask);

            var envelope = await client.ReceiveAsync();
            Assert.Equal(MessageKind.Rejected, envelope!.Kind);
            var rejected = envelope.Unwrap<RejectedMessage>()!;
            Assert.Equal(RejectReason.VersionUnsupported, rejected.Code);
            Assert.Contains("9999", rejected.Reason);
        }
    }

    [Fact]
    public async Task The_Client_Surfaces_A_Rejection_As_A_Typed_Exception()
    {
        var (client, server) = DuplexPair();
        await using (client)
        await using (server)
        {
            var serverTask = RemoteHandshake.ServerAsync(
                server, "remotehost", "9.9.9", vetoReason: RejectReason.Revoked, vetoDetail: "serial ABC was revoked");

            var ex = await Assert.ThrowsAsync<RemoteHandshake.RejectedException>(
                () => RemoteHandshake.ClientAsync(client, "1.2.3"));

            Assert.Equal(RejectReason.Revoked, ex.Code);
            Assert.Contains("ABC", ex.Message);
            // A revoked credential will not start working again; reconnecting would only feed
            // the hub's rate limiter.
            Assert.False(ex.IsRetryable);
            Assert.Null(await serverTask);
        }
    }

    [Theory]
    [InlineData(RejectReason.TooManySessions, true)]
    [InlineData(RejectReason.RateLimited, true)]
    [InlineData(RejectReason.RemoteDisabled, true)]
    [InlineData(RejectReason.Internal, true)]
    // Transient by definition: a congested tunnel, a wifi flap mid-TLS, or a momentarily busy
    // hub. Classified permanent, it retired the client until the app was restarted -- and this
    // theory used to list every code EXCEPT this one, so nothing noticed.
    [InlineData(RejectReason.HandshakeTimeout, true)]
    [InlineData(RejectReason.Revoked, false)]
    [InlineData(RejectReason.VersionUnsupported, false)]
    [InlineData(RejectReason.NotPermittedOnTransport, false)]
    public void Retryability_Distinguishes_Transient_Refusals_From_Permanent_Ones(string code, bool retryable)
    {
        Assert.Equal(retryable, new RemoteHandshake.RejectedException(code, null).IsRetryable);
    }

    [Fact]
    public void Every_Reject_Code_Has_A_Stated_Retryability()
    {
        // The omission above was invisible because the theory enumerated codes by hand. Anchor
        // it to the source of truth instead: a new RejectReason now fails here until someone
        // decides, deliberately, which side of the line it sits on. Defaulting to permanent is
        // the dangerous direction -- it does not degrade a session, it ends the client.
        var declared = typeof(RejectReason)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        var covered = typeof(RemoteHandshakeTests)
            .GetMethod(nameof(Retryability_Distinguishes_Transient_Refusals_From_Permanent_Ones))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false)
            .Cast<InlineDataAttribute>()
            .Select(d => (string)d.GetData(null!).First()[0]!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(declared);
        var missing = declared.Where(c => !covered.Contains(c)).ToList();
        Assert.True(missing.Count == 0,
            $"RejectReason code(s) with no stated retryability: {string.Join(", ", missing)}. " +
            "Add an InlineData row deciding whether a client should retry or give up.");
    }

    [Fact]
    public async Task Enrollment_Is_Refused_On_A_Remote_Transport()
    {
        // Credential issuance is pipe-only by construction. A remote peer able to mint further
        // credentials would turn one leaked build certificate into an unbounded grant.
        var (client, server) = DuplexPair();
        await using (client)
        await using (server)
        {
            var serverTask = RemoteHandshake.ServerAsync(server, "remotehost", "9.9.9");
            await client.SendAsync(MessageKind.EnrollRequest, new EnrollRequestMessage
            {
                TargetName = "give-me-another", RequestedDays = 3650,
            });

            Assert.Null(await serverTask);

            var rejected = (await client.ReceiveAsync())!.Unwrap<RejectedMessage>()!;
            Assert.Equal(RejectReason.NotPermittedOnTransport, rejected.Code);
        }
    }

    [Fact]
    public async Task Anything_Other_Than_Hello_Is_Refused_First()
    {
        // A peer must not be able to skip straight to Register and bypass version and
        // capability negotiation.
        var (client, server) = DuplexPair();
        await using (client)
        await using (server)
        {
            var serverTask = RemoteHandshake.ServerAsync(server, "remotehost", "9.9.9");
            await client.SendAsync(MessageKind.Register, new RegisterMessage { ClientId = "sneaky" });

            Assert.Null(await serverTask);
            Assert.Equal(RejectReason.NotPermittedOnTransport,
                (await client.ReceiveAsync())!.Unwrap<RejectedMessage>()!.Code);
        }
    }

    [Fact]
    public async Task A_Peer_That_Vanishes_MidHandshake_Is_Handled_Quietly()
    {
        // No exception to log and nothing to tell anyone: it is already gone.
        var (client, server) = DuplexPair();
        await using (server)
        {
            var serverTask = RemoteHandshake.ServerAsync(server, "remotehost", "9.9.9");
            await client.DisposeAsync();
            Assert.Null(await serverTask);
        }
    }

    [Fact]
    public async Task The_Client_Refuses_A_Hub_Speaking_An_Impossible_Version()
    {
        // Symmetry: version compatibility is checked in BOTH directions.
        var (client, server) = DuplexPair();
        await using (client)
        await using (server)
        {
            var clientTask = RemoteHandshake.ClientAsync(client, "1.2.3");

            await server.ReceiveAsync();
            await server.SendAsync(MessageKind.Welcome, new WelcomeMessage
            {
                ProtocolVersion = 9999, Host = "remotehost",
            });

            await Assert.ThrowsAsync<ProtocolException>(() => clientTask);
        }
    }

    [Fact]
    public async Task The_Client_Refuses_An_Unexpected_Reply()
    {
        var (client, server) = DuplexPair();
        await using (client)
        await using (server)
        {
            var clientTask = RemoteHandshake.ClientAsync(client, "1.2.3");

            await server.ReceiveAsync();
            await server.SendAsync(MessageKind.Heartbeat, new HeartbeatMessage { ClientId = "remotehost" });

            await Assert.ThrowsAsync<ProtocolException>(() => clientTask);
        }
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
