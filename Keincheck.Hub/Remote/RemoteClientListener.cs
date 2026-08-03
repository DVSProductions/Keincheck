using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Keincheck.Protocol;
using Keincheck.Remote;

namespace Keincheck.Hub.Remote;

/// <summary>
/// Accepts remote clients over mutually-authenticated TLS and hands each established session
/// to the broker, exactly as the pipe accept-loop does for local ones.
/// </summary>
/// <remarks>
/// <para>
/// There is one registry and one broker; this is simply a second way in. That keeps client-id
/// uniqueness, active-client selection, <c>ListClients()</c> (which the auto-updater consults
/// before restarting the hub) and the audit trail correct by construction rather than by
/// re-deriving them across two brokers.
/// </para>
/// <para>
/// Unlike the pipe, this listener faces peers that have not authenticated yet, so it is
/// deliberately stingy with them: a bounded number of concurrent handshakes, a short deadline,
/// a per-peer failure backoff, and small framing limits until the session is accepted.
/// </para>
/// </remarks>
public sealed class RemoteClientListener : IAsyncDisposable
{
    /// <summary>How many peers may be mid-handshake at once before new connections are dropped.</summary>
    public const int MaxPendingSessions = 8;

    /// <summary>Consecutive failures from one peer address before it is briefly refused.</summary>
    public const int FailuresBeforeBackoff = 5;

    /// <summary>How long a peer stays refused after tripping the failure threshold.</summary>
    public static readonly TimeSpan BackoffWindow = TimeSpan.FromSeconds(30);

    private readonly PipeClientBroker _broker;
    private readonly RemoteStore _store;
    private readonly HubAuditLog _audit;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, FailureRecord> _failures = new(StringComparer.Ordinal);

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _pending;
    private int _disposed;

    // In-flight handshake tasks, so shutdown can wait for them instead of disposing the
    // cancellation source and the certificates out from under them.
    private readonly object _inFlightGate = new();
    private readonly HashSet<Task> _inFlight = [];

    public RemoteClientListener(
        PipeClientBroker broker, RemoteStore store, HubAuditLog audit, Action<string>? log = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _log = log;
    }

    /// <summary>The endpoint actually bound, or null when the listener is not running.</summary>
    public IPEndPoint? BoundEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    /// <summary>Whether the listener is currently accepting.</summary>
    public bool IsRunning => _acceptLoop is not null;

    /// <summary>
    /// Starts accepting, if remote access is enabled and provisioned.
    /// </summary>
    /// <remarks>
    /// Fail-closed in both directions. A hub whose operator never enabled remote access does
    /// not open a socket; a hub with no certificate authority cannot open one either, because
    /// a listener with nothing to authenticate against would accept whatever turned up.
    /// </remarks>
    /// <returns>True if it started; false if remote is disabled or unprovisioned.</returns>
    public bool TryStart()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var settings = _store.Settings;

        if (_acceptLoop is not null)
        {
            // Already listening. Report whether it is listening on what was ASKED for, because
            // "yes it's up" while bound to the previous address is worse than a plain refusal:
            // newly-issued credentials advertise the new port, so every client enrolled before
            // the next hub restart would dial a port nothing is bound to, while the UI insists
            // remote access is fine.
            var bound = BoundEndpoint;
            var matches = bound is not null
                && (settings.Port == 0 || bound.Port == settings.Port)
                && string.Equals(bound.Address.ToString(), settings.BindAddress, StringComparison.OrdinalIgnoreCase);
            if (!matches)
                _log?.Invoke($"already listening on {bound}; restart the hub to move to {settings.BindAddress}:{settings.Port}.");
            return matches;
        }
        if (!settings.Enabled)
            return false;

        if (!_store.IsProvisioned)
        {
            _log?.Invoke("remote access is enabled but no certificate authority exists; not listening.");
            return false;
        }

        if (!IPAddress.TryParse(settings.BindAddress, out var bind))
        {
            _log?.Invoke($"'{settings.BindAddress}' is not a valid bind address; not listening.");
            return false;
        }

        try
        {
            _listener = StreamTransport.CreateListener(bind, settings.Port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _log?.Invoke($"could not bind {bind}:{settings.Port} — {ex.Message}");
            _listener = null;
            return false;
        }

        var where = BoundEndpoint?.ToString() ?? $"{bind}:{settings.Port}";
        _log?.Invoke(IPAddress.Any.Equals(bind) || IPAddress.IPv6Any.Equals(bind)
            ? $"listening on {where} (ALL interfaces; mutual TLS is the only barrier)"
            : $"listening on {where}");

        _acceptLoop = Task.Run(AcceptLoopAsync);
        return true;
    }

    /// <summary>Stops accepting. Existing sessions continue until they end on their own.</summary>
    public async Task StopAsync()
    {
        var loop = _acceptLoop;
        _acceptLoop = null;
        if (loop is null)
            return;

        try { _listener?.Stop(); } catch { /* already down */ }
        _listener = null;
        try { await loop.ConfigureAwait(false); } catch { /* expected on shutdown */ }
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener;
        while (listener is not null && !_cts.IsCancellationRequested)
        {
            try
            {
                // AcceptAsync completes the TLS handshake, so it is NOT safe to await here --
                // a peer that connects and then says nothing would stall every other client.
                // Accept the socket first, and hand the handshake to its own task.
                var accepted = await listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                Track(HandleAsync(accepted));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // StopAsync
            }
            catch (Exception ex)
            {
                // The accept loop must never die: one malformed connection cannot be allowed
                // to take remote access down until the hub restarts.
                _log?.Invoke($"accept failed: {ex.Message}");
                try { await Task.Delay(200, _cts.Token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    /// <summary>Registers a session task so shutdown can wait for it, and unregisters on completion.</summary>
    private void Track(Task session)
    {
        lock (_inFlightGate)
            _inFlight.Add(session);

        _ = session.ContinueWith(t =>
        {
            lock (_inFlightGate)
                _inFlight.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        // Read the peer address INSIDE the try: on a socket that died between accept and here,
        // RemoteEndPoint throws, and outside the try that would leak the TcpClient and leave
        // the exception unobserved.
        string peer;
        try
        {
            peer = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            tcp.Dispose();
            return;
        }


        // Cheap rejections first, before spending a TLS handshake on them.
        if (Interlocked.Increment(ref _pending) > MaxPendingSessions)
        {
            Interlocked.Decrement(ref _pending);
            _log?.Invoke($"refused {peer}: too many sessions mid-handshake.");
            tcp.Dispose();
            return;
        }

        // The slot is released as soon as the handshake finishes, NOT when the session ends.
        // It bounds how many UNAUTHENTICATED peers can be in flight; holding it for the life of
        // an established session would instead cap the hub at MaxPendingSessions connected
        // clients and silently refuse every one after that.
        var released = false;
        void ReleaseSlot()
        {
            if (released) return;
            released = true;
            Interlocked.Decrement(ref _pending);
        }

        try
        {
            if (IsBackedOff(peer))
            {
                _log?.Invoke($"refused {peer}: backing off after repeated failures.");
                tcp.Dispose();
                return;
            }

            await HandshakeAndServeAsync(tcp, peer, ReleaseSlot).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsShuttingDown(ex))
        {
            // The hub is stopping, not being attacked. Recording this as an authentication
            // failure would put a LEGITIMATE client in the security audit trail with a cause
            // ("Cannot access a disposed object") that has nothing to do with authentication —
            // and every hub restart would leave a few of them behind to mislead whoever reads
            // the log later.
            _log?.Invoke($"dropped the session from {peer} during shutdown.");
            try { tcp.Dispose(); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            RecordFailure(peer);
            // Every failed authentication is recorded. A peer repeatedly failing to
            // authenticate is the signal that someone is probing the listener, and it is
            // invisible without this.
            _audit.Add(RemoteAudit.Entry(
                AuditKind.AuthFailure, $"rejected connection from {peer}",
                transport: ClientTransport.Tcp, error: ex.Message));
            _log?.Invoke($"session from {peer} failed: {ex.Message}");
            Debug.WriteLine($"[Hub.Remote] {ex}");
            try { tcp.Dispose(); } catch { /* ignore */ }
        }
        finally
        {
            ReleaseSlot();
        }
    }

    private async Task HandshakeAndServeAsync(TcpClient tcp, string peer, Action releaseSlot)
    {
        using var handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        handshakeDeadline.CancelAfter(RemoteHandshake.Timeout);

        StreamTransport.Configure(tcp.Client);
        var network = tcp.GetStream();

        var (ssl, peerCertificate) = await RemoteTls.AuthenticateAsServerAsync(
            network, _store.ServerCertificate, _store.CertificateAuthority,
            handshakeDeadline.Token).ConfigureAwait(false);

        PipeChannel channel;
        string host;
        string serial;
        using (peerCertificate)
        {
            host = RemoteCertificates.CommonNameOf(peerCertificate);
            serial = RemoteCertificates.SerialOf(peerCertificate);
            // Small framing limits until the session is accepted: TLS proved WHO the peer is,
            // not that it is still allowed in.
            channel = new PipeChannel(ssl, ownsStream: true, ChannelLimits.Handshake);
        }

        try
        {
            // Revocation is checked on every connect, so revoking takes effect at the peer's
            // next reconnect rather than whenever its certificate happens to expire. An
            // UNKNOWN serial is refused too: a validly-signed certificate the hub has no
            // record of means the issued list was lost or the CA key leaked.
            string? veto = null;
            string? vetoDetail = null;
            if (!_store.Settings.Enabled)
            {
                veto = RejectReason.RemoteDisabled;
                vetoDetail = "Remote access was disabled on the hub.";
            }
            else if (_store.IsRevoked(serial))
            {
                veto = RejectReason.Revoked;
                vetoDetail = $"Credential {serial} for '{host}' is revoked or unknown to this hub.";
            }

            var result = await RemoteHandshake.ServerAsync(
                channel, host, HubMetaTools.ResolveHubAssemblyVersion(),
                veto, vetoDetail, _cts.Token).ConfigureAwait(false);

            if (result is null)
            {
                if (veto is not null)
                {
                    RecordFailure(peer);
                    _audit.Add(RemoteAudit.Entry(
                        AuditKind.AuthFailure, $"refused '{host}' from {peer}",
                        host: host, transport: ClientTransport.Tcp, error: vetoDetail));
                    _log?.Invoke($"refused {host} from {peer}: {vetoDetail}");
                }
                await channel.DisposeAsync().ConfigureAwait(false);
                return;
            }

            ClearFailures(peer);

            // Authenticated and accepted: it is a client now, not a pending handshake.
            releaseSlot();

            _audit.Add(RemoteAudit.Entry(
                AuditKind.Attach, $"'{host}' attached from {peer} (credential {serial})",
                host: host, transport: ClientTransport.Tcp));
            _log?.Invoke($"attached {host} from {peer} (serial {serial}).");

            var context = new ClientSessionContext
            {
                Transport = ClientTransport.Tcp,
                Host = host,
                PeerAddress = peer,
                // Looking is always safe; driving is a per-session escalation the operator makes.
                ReadOnlyDefault = true,
                // The process is on another machine -- the hub cannot start or stop it.
                CanLaunch = false,
            };

            // Hub -> client heartbeats, for as long as the session lasts. The Welcome we just
            // sent PROMISED these, and the client sets its idle-read deadline from that
            // promise -- so not running this pump silently kills every remote session the
            // moment it goes quiet between tool calls.
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var heartbeat = result.ServerHeartbeat is { } interval
                ? HeartbeatLoopAsync(channel, interval, sessionCts.Token)
                : Task.CompletedTask;

            try
            {
                // The broker owns the session from here, exactly as it does for a pipe client.
                await _broker.AcceptChannel(channel, context, _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                sessionCts.Cancel();
                try { await heartbeat.ConfigureAwait(false); } catch { /* stopping */ }
            }

            _audit.Add(RemoteAudit.Entry(
                AuditKind.Detach, $"'{host}' detached from {peer}",
                host: host, transport: ClientTransport.Tcp));
            _log?.Invoke($"detached {host} from {peer}.");
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends a heartbeat to the client every <paramref name="interval"/> until the session
    /// ends, so a quiet-but-healthy link is distinguishable from a dead one.
    /// </summary>
    /// <remarks>
    /// Safe to run concurrently with the broker's own writes on this channel:
    /// <c>PipeChannel.SendAsync</c> serialises writers, so a heartbeat can never interleave
    /// its chunks with a tool result.
    /// </remarks>
    private static async Task HeartbeatLoopAsync(PipeChannel channel, TimeSpan interval, CancellationToken ct)
    {
        var sequence = 0L;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await channel.SendAsync(MessageKind.Heartbeat, new HeartbeatMessage
                {
                    Sequence = Interlocked.Increment(ref sequence),
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }, cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ending.
        }
        catch (Exception ex)
        {
            // The link is gone. The receive loop will notice and unwind the session; there is
            // nothing useful to do here beyond stopping.
            Debug.WriteLine($"[Hub.Remote] heartbeat stopped: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- rate limiting

    private sealed class FailureRecord
    {
        public int Count;
        public DateTimeOffset BlockedUntil;
        public DateTimeOffset LastSeen = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// How long a peer's failure record is kept after its last failure.
    /// </summary>
    /// <remarks>
    /// Records used to be removed only on a <i>successful</i> handshake, so a peer that never
    /// succeeded left one behind forever. Bound to a public interface for months — the runtime
    /// this is designed for — that is one permanent entry per distinct probing source address,
    /// and over IPv6 an attacker on a /64 has effectively unlimited addresses.
    /// </remarks>
    public static readonly TimeSpan FailureRetention = TimeSpan.FromMinutes(30);

    /// <summary>How many failure records to keep before pruning aggressively.</summary>
    public const int MaxTrackedPeers = 1024;

    private bool IsBackedOff(string peer)
        => _failures.TryGetValue(peer, out var record) && DateTimeOffset.UtcNow < record.BlockedUntil;

    private void RecordFailure(string peer)
    {
        var record = _failures.GetOrAdd(peer, _ => new FailureRecord());
        lock (record)
        {
            record.Count++;
            record.LastSeen = DateTimeOffset.UtcNow;
            if (record.Count >= FailuresBeforeBackoff)
            {
                record.BlockedUntil = DateTimeOffset.UtcNow + BackoffWindow;
                record.Count = 0;
            }
        }

        PruneFailures();
    }

    private void ClearFailures(string peer) => _failures.TryRemove(peer, out _);

    /// <summary>Drops records that are stale and no longer blocking anyone.</summary>
    private void PruneFailures()
    {
        if (_failures.Count <= MaxTrackedPeers)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var (peer, record) in _failures)
        {
            // Never drop a record that is currently enforcing a backoff — that would hand a
            // probing peer a way to clear its own penalty by making more noise.
            if (now >= record.BlockedUntil && now - record.LastSeen > FailureRetention)
                _failures.TryRemove(peer, out _);
        }
    }

    /// <summary>True when an exception is just the listener being torn down.</summary>
    private bool IsShuttingDown(Exception ex) =>
        _cts.IsCancellationRequested
        && ex is OperationCanceledException or ObjectDisposedException;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        await StopAsync().ConfigureAwait(false);

        // Wait for in-flight handshakes before disposing _cts and letting the caller dispose
        // the certificate store. They read _cts.Token and use the CA and server certificates;
        // pulling those out from under them turns an orderly shutdown into a burst of spurious
        // errors, and (for the certificates) a use-after-dispose inside TLS.
        Task[] pending;
        lock (_inFlightGate)
            pending = _inFlight.ToArray();

        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // Best effort: a handshake wedged on a peer that stopped responding must not
                // hold up hub shutdown indefinitely.
            }
        }

        _cts.Dispose();
    }
}
