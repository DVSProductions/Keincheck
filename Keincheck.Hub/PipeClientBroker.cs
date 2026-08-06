using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Keincheck.Protocol;

namespace Keincheck.Hub;

/// <summary>
/// Options that tune the live <see cref="PipeClientBroker"/>.
/// </summary>
public sealed class BrokerOptions
{
    /// <summary>The control pipe to listen on. Defaults to <see cref="PipeNames.ControlPipe"/>.</summary>
    public string? PipeName { get; set; }

    /// <summary>
    /// How long a client may go without a heartbeat (or any message) before the hub
    /// declares it down and tears the session. Should be a few heartbeat intervals.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often the watchdog scans for stale clients.</summary>
    public TimeSpan WatchdogInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long an invoked tool may run before the call is failed. Default 60s.</summary>
    public TimeSpan InvokeTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a <b>non-pipe</b> session may stay connected without registering (or asking
    /// to enroll) before the hub drops it. <see cref="Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    /// <remarks>
    /// The heartbeat watchdog only sees clients that reached the registry, so until a session
    /// registers nothing is watching it at all. A remote peer that completes the TLS handshake
    /// and then simply says nothing would otherwise park in <c>ReceiveAsync</c> for the hub's
    /// entire lifetime, holding a socket, an <c>SslStream</c>, a session task and a heartbeat
    /// pump that keeps writing every few seconds — and leaving an <c>Attach</c> audit entry
    /// with no matching <c>Detach</c>. Repeat at will and it is an unbounded leak.
    /// Deliberately generous: it only has to be shorter than "forever".
    /// </remarks>
    public TimeSpan RegistrationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, the first instance of an app is assigned the bare id
    /// <c>#1</c> suffix (e.g. <c>MyApp#1</c>); always-suffixed keeps ids predictable.
    /// </summary>
    public bool AlwaysSuffixInstance { get; set; } = true;

    /// <summary>
    /// How long the hub remembers that it launched a process, waiting for that process to
    /// register so the instance can be associated with the agent session that asked for it.
    /// </summary>
    /// <remarks>
    /// A launch that never registers (the app crashed on startup, or it is simply slow) must
    /// not leave an entry behind forever: the pending table is matched on process id as well
    /// as launch token, and a stale entry is how an unrelated process that later happens to
    /// reuse that pid would be handed someone else's claim.
    /// </remarks>
    public TimeSpan LaunchRegisterTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Test seam: how a launch actually starts a process. Null (the default) uses
    /// <see cref="Process.Start(ProcessStartInfo)"/>.
    /// </summary>
    internal Func<ProcessStartInfo, int>? ProcessStarter { get; set; }
}

/// <summary>
/// The live <see cref="IClientBroker"/>: a named-pipe <b>server</b> that accepts client
/// connections, tracks them in a registry (live + persisted-known), assigns each a
/// stable hub id (<c>AppId#n</c> with reserved slots across restart), correlates
/// <c>InvokeTool</c>↔<c>ToolResult</c> over the pipe, and launches/restarts apps from
/// their recorded profiles. Replaces the spike's <see cref="StubClientBroker"/>.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> The registry is guarded by a single lock; per-client receive
/// loops run on the thread pool. Events (<see cref="ClientConnected"/> etc.) are raised
/// off arbitrary threads — subscribers (the tray UI) marshal to the UI thread
/// themselves. Pipe writes are serialized inside each <see cref="PipeChannel"/>.</para>
/// <para><b>Testability.</b> <see cref="AcceptChannel"/> drives the per-client receive
/// loop over any <see cref="PipeChannel"/> (e.g. an in-memory pair), so the registry +
/// correlation can be unit-tested without a real named pipe.</para>
/// </remarks>
public sealed class PipeClientBroker : IClientBroker, IAsyncDisposable
{
    private readonly BrokerOptions _options;
    private readonly KnownClientStore _store;
    private readonly HubAuditLog _audit;
    private readonly string _pipeName;

    private readonly object _gate = new();
    // Hub-id -> live session (connected clients only).
    private readonly Dictionary<string, LiveClient> _live = new(StringComparer.Ordinal);
    // AppId -> set of reserved hub-ids (across the run) so a restart keeps its slot.
    private readonly Dictionary<string, SortedSet<int>> _reservedSuffixes = new(StringComparer.OrdinalIgnoreCase);
    // Hub-id -> last-known snapshot for disconnected-but-seen clients.
    private readonly Dictionary<string, ClientInfo> _seen = new(StringComparer.Ordinal);
    // Launch id -> the launch we are still waiting to see register. Swept by the watchdog so
    // a launch that never registers cannot leave an entry that a later, unrelated process
    // reusing that pid would match against.
    private readonly Dictionary<string, PendingLaunch> _pendingLaunches = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private Task? _watchdog;
    // The hub-wide default selection: the seed a newly connected agent session starts from,
    // and the tray's notion of "the" client. NOT the routing authority — each MCP session
    // carries its own selection (see HubSession).
    private string? _default;
    // The hub-id that was the default when it last disconnected. Lets a reconnecting client
    // reclaim the default automatically (finding-1) — but only while no other client has been
    // made default in the meantime, so a deliberate manual selection is respected.
    private string? _lastDefault;
    private int _disposed;

    // Null unless remote access is set up. A hub with no issuer refuses every enrollment
    // request, which is the correct default for one whose owner never asked for remote access.
    private volatile Remote.ICredentialIssuer? _credentialIssuer;

    /// <summary>Creates the broker. Call <see cref="Start"/> to begin accepting clients.</summary>
    public PipeClientBroker(BrokerOptions? options = null, KnownClientStore? store = null, HubAuditLog? audit = null)
    {
        _options = options ?? new BrokerOptions();
        _store = store ?? KnownClientStore.Open();
        _audit = audit ?? new HubAuditLog();
        _pipeName = _options.PipeName ?? PipeNames.ControlPipe;

        // Seed the registry with persisted-known apps so they appear in
        // ListKnownClients()/can be launched before they have ever connected this run.
        foreach (var profile in _store.All())
        {
            var hubId = ReserveSuffix(profile.AppId, preferred: 1);
            var isRemote = !string.IsNullOrEmpty(profile.Host);
            _seen[hubId] = new ClientInfo
            {
                ClientId = hubId,
                // profile.AppId is the persistence key, which for a remote entry is the
                // composite AppId@Host. Split it back apart so the bare app id is reported.
                AppId = isRemote ? StripHost(profile.AppId, profile.Host!) : profile.AppId,
                DisplayName = profile.DisplayName ?? profile.AppId,
                IsConnected = false,
                ReadOnly = profile.ReadOnly,
                ExecutablePath = profile.ExecutablePath,
                LastSeenUtc = profile.LastSeenUtc,
                // A restored remote entry must NOT look local: CanLaunch defaults to true, and
                // a remembered-but-offline remote app is exactly the case where an operator
                // reaches for hub_launch_client. Getting this wrong would start a local
                // process for a machine that is not here.
                Host = profile.Host,
                Transport = isRemote ? ClientTransport.Tcp : ClientTransport.Pipe,
                CanLaunch = !isRemote,
            };
        }
    }

    /// <summary>The registry/persistence key for a client: <c>AppId</c>, or <c>AppId@Host</c> when remote.</summary>
    internal static string IdentityOf(string appId, string? host) =>
        string.IsNullOrEmpty(host) ? appId : $"{appId}@{host}";

    /// <summary>Recovers the bare app id from a composite <c>AppId@Host</c> persistence key.</summary>
    private static string StripHost(string identity, string host)
    {
        var suffix = "@" + host;
        return identity.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? identity[..^suffix.Length]
            : identity;
    }

    /// <summary>The audit log of AI tool calls; bound by the tray UI.</summary>
    public HubAuditLog Audit => _audit;

    /// <summary>The persisted known-client store.</summary>
    public KnownClientStore Store => _store;

    /// <summary>The control pipe this broker listens on.</summary>
    public string PipeName => _pipeName;

    /// <summary>
    /// Who issues remote credentials for <see cref="MessageKind.EnrollRequest"/> arriving on
    /// the local pipe. Null (the default) refuses every request.
    /// </summary>
    /// <remarks>
    /// Settable so the hub can switch remote access on and off at runtime without restarting
    /// the broker or dropping connected clients.
    /// </remarks>
    public Remote.ICredentialIssuer? CredentialIssuer
    {
        get => _credentialIssuer;
        set => _credentialIssuer = value;
    }

    /// <summary>Starts the pipe accept loop and the heartbeat watchdog.</summary>
    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));
        _watchdog ??= Task.Run(() => WatchdogLoopAsync(_cts.Token));
    }

    // ===================================================================== accept

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PipeChannel channel;
            try
            {
                var server = PipeTransport.CreateServerStream(_pipeName);
                channel = await PipeTransport.AcceptAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Hub] accept failed: {ex.Message}");
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }

            // Service this connection concurrently; loop back to accept the next.
            _ = ServeClientAsync(channel, ClientSessionContext.LocalPipe, ct);
        }
    }

    /// <summary>
    /// Drives the per-client receive loop over an already-connected
    /// <see cref="PipeChannel"/>. Public for tests (in-memory pipe pair); the accept
    /// loop calls it for real connections.
    /// </summary>
    public Task AcceptChannel(PipeChannel channel, CancellationToken cancellationToken = default)
        => ServeClientAsync(channel, ClientSessionContext.LocalPipe, cancellationToken);

    /// <summary>
    /// Drives the receive loop for a session whose transport-level facts are already
    /// established. A remote listener passes the host it read off the validated certificate,
    /// so the registry records where a client is from rather than taking its word for it.
    /// </summary>
    public Task AcceptChannel(
        PipeChannel channel, ClientSessionContext context, CancellationToken cancellationToken = default)
        => ServeClientAsync(channel, context ?? ClientSessionContext.LocalPipe, cancellationToken);

    private async Task ServeClientAsync(PipeChannel channel, ClientSessionContext context, CancellationToken ct)
    {
        LiveClient? client = null;

        // Until this session registers, the heartbeat watchdog cannot see it — it only scans
        // the registry. So a remote peer that finishes the handshake and then goes quiet is
        // watched by nothing. Give the unregistered phase its own deadline; it is dropped as
        // soon as the client is in the registry, from which point the watchdog owns liveness.
        // The local pipe is exempt: it is CurrentUserOnly, so a peer that could wedge a session
        // there can already do strictly worse things directly.
        var registrationDeadline =
            context.Transport != ClientTransport.Pipe && _options.RegistrationTimeout > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
        registrationDeadline?.CancelAfter(_options.RegistrationTimeout);

        try
        {
            var receiveToken = registrationDeadline?.Token ?? ct;

            while (!ct.IsCancellationRequested)
            {
                var envelope = await channel.ReceiveAsync(receiveToken).ConfigureAwait(false);
                if (envelope is null)
                    break; // clean EOF — client closed the pipe

                switch (envelope.Kind)
                {
                    case MessageKind.Register:
                        // Exactly one registration per connection. A client that wants a fresh
                        // identity reconnects; nothing legitimate re-registers on a live
                        // session. Without this, one authenticated peer could stream Register
                        // frames and mint a LiveClient per frame — and because suffix
                        // allocation probes the live set linearly while holding the registry
                        // lock, a few thousand of them stall every other caller (MCP dispatch,
                        // the pipe accept loop, the tray) for seconds at a time.
                        if (client is not null)
                            throw new ProtocolException(
                                $"Client '{client.ClientId}' sent a second Register on one session.");
                        client = HandleRegister(channel, context, envelope);

                        // Registered: the watchdog can see it now, so retire the deadline and
                        // stop reading against a token that is about to fire.
                        registrationDeadline?.CancelAfter(Timeout.InfiniteTimeSpan);
                        receiveToken = ct;
                        break;

                    case MessageKind.ToolList:
                        HandleToolList(client, envelope);
                        break;

                    case MessageKind.Heartbeat:
                        Touch(client);
                        break;

                    case MessageKind.ToolResult:
                        HandleToolResult(client, envelope);
                        break;

                    case MessageKind.ClientDown:
                        HandleClientDown(client, envelope);
                        return;

                    case MessageKind.EnrollRequest:
                        // An enrollment session is not a client session: it asks one question,
                        // gets one answer, and closes. It never registers, so it never appears
                        // in the registry.
                        await HandleEnrollAsync(channel, context, envelope, ct).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (registrationDeadline?.IsCancellationRequested == true
                                                 && !ct.IsCancellationRequested)
        {
            // Not a shutdown: this peer authenticated and then never registered.
            Debug.WriteLine(
                $"[Hub] dropping an unregistered {context.Transport} session from " +
                $"'{context.Host ?? context.PeerAddress ?? "?"}' after {_options.RegistrationTimeout}.");
            _audit.Add(Remote.RemoteAudit.Entry(
                AuditKind.AuthFailure,
                $"dropped an unregistered session from {context.Host ?? context.PeerAddress ?? "?"}",
                host: context.Host,
                transport: context.Transport,
                error: $"no Register within {_options.RegistrationTimeout}"));
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Hub] client session error: {ex.Message}");
        }
        finally
        {
            registrationDeadline?.Dispose();
            if (client is not null)
                Disconnect(client, graceful: false, reason: "transport closed");
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ===================================================================== enrollment

    /// <summary>
    /// Serves a credential-enrollment request. Local pipe only.
    /// </summary>
    /// <remarks>
    /// The transport check is the whole security property here, so it is the first thing that
    /// happens: a remote peer must never be able to mint further credentials, or one leaked
    /// build certificate becomes an unbounded, self-renewing grant. (The remote handshake
    /// already refuses this message kind before a session can even reach the broker; this is
    /// the second, independent gate — the kind of thing that must not depend on one check in
    /// one place staying correct.)
    /// </remarks>
    private async Task HandleEnrollAsync(
        PipeChannel channel, ClientSessionContext context, MessageEnvelope envelope, CancellationToken ct)
    {
        if (context.IsRemote)
        {
            await channel.SendAsync(MessageKind.Rejected, new RejectedMessage
            {
                Code = RejectReason.NotPermittedOnTransport,
                Reason = "Credentials can only be issued over the local control pipe.",
            }, envelope.CorrelationId, ct).ConfigureAwait(false);
            return;
        }

        EnrollResponseMessage response;
        var issuer = _credentialIssuer;
        if (issuer is null)
        {
            response = new EnrollResponseMessage
            {
                Accepted = false,
                Reason = "This hub cannot issue remote credentials. Enable remote access in the hub window.",
            };
        }
        else
        {
            var request = envelope.Unwrap<EnrollRequestMessage>();
            response = request is null
                ? new EnrollResponseMessage { Accepted = false, Reason = "The enrollment request was empty." }
                : issuer.Issue(request);
        }

        await channel.SendAsync(MessageKind.EnrollResponse, response, envelope.CorrelationId, ct)
            .ConfigureAwait(false);
    }

    // ===================================================================== register

    private LiveClient HandleRegister(PipeChannel channel, ClientSessionContext context, MessageEnvelope envelope)
    {
        var reg = envelope.Unwrap<RegisterMessage>()
            ?? throw new ProtocolException("Register payload was empty.");

        if (!ProtocolVersion.IsCompatible(reg.ProtocolVersion))
            throw new ProtocolException(
                $"Client '{reg.ClientId}' speaks protocol v{reg.ProtocolVersion}; hub supports " +
                $"[{ProtocolVersion.Minimum}, {ProtocolVersion.Current}].");

        // The app id is entirely client-chosen and reaches the registry keys, the hub id, and
        // the wait-for filters, so it is sanitised rather than trusted. Two things this stops:
        // an id containing '@' or '#' can otherwise impersonate the `AppId@Host` disambiguator
        // (a remote client registering as "myapp@MACHINENAME" would be RETURNED by
        // hub_wait_for_client for that filter, and the operator's calls would go to it); and an
        // unbounded id is a free way to bloat every dictionary that keys on it.
        var appId = SanitizeAppId(reg.ClientId);

        // The suffix space is keyed on the FULL identity, not the bare app id: a local
        // 'myapp' and a remote 'myapp@MACHINENAME' are different apps that both want
        // slot #1, and sharing a key would hand them the same hub id.
        var identity = context.IsRemote && !string.IsNullOrEmpty(context.Host)
            ? $"{appId}@{context.Host}"
            : appId;

        // A process id is only meaningful on this machine. Reusing a remote client's pid for
        // the stale-session dedup below would collide with an unrelated LOCAL process.
        var localProcessId = context.IsRemote ? 0 : reg.ProcessId;
        var (path, args, cwd) = context.CanLaunch
            ? ResolveProcessProfile(reg.ProcessId)
            : (null, null, null);

        ClientInfo info;
        LiveClient live;
        LiveClient? supersededStale = null;
        PendingLaunch? launch = null;
        lock (_gate)
        {
            // Did an agent ask for this instance? Prefer the launch token — the process the
            // hub started is often not the one that registers (launcher script, dotnet host),
            // and pid matching silently fails exactly there. Pid is the fallback for clients
            // too old to echo the token. Remote sessions never match: their pid is not from
            // this machine, and the hub cannot launch them in the first place.
            if (!context.IsRemote)
            {
                launch = FindPendingLaunch_NoLock(reg.LaunchToken, localProcessId, identity);
                if (launch is not null)
                    _pendingLaunches.Remove(launch.LaunchId);
            }

            // Stale-reconnect dedup (finding-2 Fix A): if a live session for the SAME app
            // and SAME process is already registered, this Register is that process
            // re-registering (its client auto-reconnected before the old session's
            // transport-close was observed). Reuse its hub-id and evict the stale session
            // instead of minting a fresh suffix, so one process never piles up as #2/#3.
            // Only when the pid is known (>0); an unknown pid falls back to a fresh slot.
            string hubId;
            if (localProcessId > 0
                && _live.Values.FirstOrDefault(c =>
                       c.ProcessId == localProcessId
                       && !c.IsRemote
                       && string.Equals(c.AppId, appId, StringComparison.OrdinalIgnoreCase))
                   is { } stale)
            {
                hubId = stale.ClientId;
                supersededStale = stale;
                _live.Remove(hubId); // the new LiveClient below takes the same id
            }
            else
            {
                // Claim exactly the slot NextFreeSuffix picked. It already skipped every LIVE
                // client while holding this lock, so the slot is genuinely free — whereas
                // ReserveSuffix would additionally skip slots held by DISCONNECTED clients,
                // which defeats the reservation's entire purpose.
                //
                // That is a real, pre-existing bug: the constructor seeds slot 1 for every app
                // in the persisted store, so the first time a known app connected after a hub
                // restart it was handed '#2' and its '#1' sat in the list as a permanent ghost.
                hubId = ClaimSuffix(identity, NextFreeSuffix(identity));
            }

            var existingReadOnly =
                (supersededStale?.ReadOnly ?? false)
                || (_seen.TryGetValue(hubId, out var prior) && prior.ReadOnly);

            // The persisted decision is keyed on the FULL identity, so the remote machine and the copy of
            // the same app on this desk keep separate settings.
            var persisted = _store.Get(identity);

            // A remembered decision WINS over the transport's default — that is what makes
            // "let me drive the remote machine" survive the wifi dropping out. Only a client the operator
            // has never ruled on falls back to read-only-because-remote. (Note this reads the
            // persisted value directly rather than OR-ing it: an OR could never express
            // "explicitly allowed", which is the whole point.)
            bool readOnly;
            if (persisted is not null)
                readOnly = persisted.ReadOnly || existingReadOnly;
            else
                readOnly = context.ReadOnlyDefault || existingReadOnly;

            live = new LiveClient(hubId, appId, channel)
            {
                DisplayName = reg.DisplayName ?? appId,
                ProcessId = reg.ProcessId,
                ExecutablePath = path,
                Arguments = args,
                WorkingDirectory = cwd,
                // Remote starts read-only until the operator says otherwise; their decision is
                // then remembered per machine (see above).
                ReadOnly = readOnly,
                OwnsWindows = reg.OwnsWindows,
                ClientVersion = reg.ClientVersion,
                ConnectedAtUtc = DateTimeOffset.UtcNow,
                Transport = context.Transport,
                Host = context.Host,
                MachineId = context.MachineId ?? (context.IsRemote ? null : Environment.MachineName),
                CanLaunch = context.CanLaunch,
                IdentityKey = identity,
                LaunchId = launch?.LaunchId,
                LaunchSessionId = launch?.SessionId,
                LaunchSessionLabel = launch?.SessionLabel,
            };
            live.Touch();
            _live[hubId] = live;
            _seen.Remove(hubId); // now live, not merely seen
            info = Snapshot_NoLock(live);
        }

        // Tear down the superseded session's transport so its receive loop unwinds. Its
        // eventual finally{Disconnect} no-ops because Disconnect re-checks ReferenceEquals
        // against the live entry, which now points at the NEW session that reclaimed the id.
        if (supersededStale is not null)
        {
            try { supersededStale.Channel.Dispose(); } catch { /* already dead */ }
            foreach (var kv in supersededStale.Pending)
            {
                if (supersededStale.Pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new IOException(
                        $"Client '{supersededStale.ClientId}' re-registered; superseding the prior session."));
            }
        }

        // Persist under the full identity. A remote entry deliberately carries NO executable
        // path or arguments — there is no local process to start, and recording one is how a
        // hub_launch_client for that id would end up starting a local copy of an app that only
        // ever ran elsewhere. Its Host is what marks it remote on restore.
        _store.Upsert(new KnownClientProfile
        {
            AppId = identity,
            Host = context.Host,
            DisplayName = live.DisplayName,
            ExecutablePath = context.CanLaunch ? path : null,
            Arguments = context.CanLaunch ? args : null,
            WorkingDirectory = context.CanLaunch ? cwd : null,
            ReadOnly = live.ReadOnly,
            LastSeenUtc = DateTimeOffset.UtcNow,
        });

        // Auto-activate so the AI sees tools immediately, without a manual
        // hub_select_client. Two cases, both gated on no client currently being active:
        //   - the very first client this run (_lastDefault is null), as before; or
        //   - the SAME client that was active when it dropped reclaiming its slot
        //     (finding-1 auto-reselect). A DIFFERENT client reconnecting must NOT steal
        //     active away from a deliberate manual selection made while the first was
        //     down — which is why we require live.ClientId == _lastDefault here.
        //
        // A REMOTE client never auto-activates, even as the first client of a run. Tool calls
        // are routed to whichever client is active, so auto-activating on connect would mean
        // that whatever attaches first receives the operator's calls — arguments included —
        // and answers them. Requiring an explicit hub_select_client keeps that a decision.
        // Reclaiming a slot it was already deliberately selected for is still allowed.
        //
        // An instance an agent asked the hub to launch is NOT a candidate: it belongs to that
        // agent, and making it the hub-wide default would hand it to whoever connects next.
        var becameActive = false;
        if (launch is null)
        {
            lock (_gate)
            {
                var mayAutoActivate = !context.IsRemote || live.ClientId == _lastDefault;
                if (mayAutoActivate && _default is null && (_lastDefault is null || live.ClientId == _lastDefault))
                {
                    _default = live.ClientId;
                    _lastDefault = null; // consumed — don't re-steal on a later reconnect
                    becameActive = true;
                }
            }
        }

        ClientConnected?.Invoke(this, info);
        if (becameActive)
            ClientUpdated?.Invoke(this, info); // nudge the MCP server to re-list
        if (launch is not null)
            LaunchRegistered?.Invoke(this, info);
        return live;
    }

    /// <summary>
    /// Matches a registration back to the launch that produced it: by token when the client
    /// echoed one, otherwise by process id. Caller holds <see cref="_gate"/>.
    /// </summary>
    private PendingLaunch? FindPendingLaunch_NoLock(string? launchToken, int processId, string identity)
    {
        if (launchToken is { Length: > 0 } token && _pendingLaunches.TryGetValue(token, out var byToken))
            return byToken;

        if (processId <= 0)
            return null;

        // Pid fallback for clients that predate the token. Require the app identity to match
        // too, so an unrelated app that happens to be started at the same moment cannot be
        // mistaken for the one that was asked for.
        return _pendingLaunches.Values.FirstOrDefault(p =>
            p.ProcessId == processId
            && string.Equals(p.Identity, identity, StringComparison.OrdinalIgnoreCase));
    }

    private void HandleToolList(LiveClient? client, MessageEnvelope envelope)
    {
        if (client is null)
            return;

        var list = envelope.Unwrap<ToolListMessage>();
        if (list is null)
            return;

        client.Touch();
        client.Tools = list.Tools;
        // Recompute window-ownership on every tool-list: a process whose windows open
        // after startup only becomes the UI-owner once they exist (finding-2 Fix B).
        client.OwnsWindows = list.OwnsWindows;

        ClientInfo info;
        lock (_gate)
            info = Snapshot_NoLock(client);
        ClientUpdated?.Invoke(this, info);
    }

    private void HandleToolResult(LiveClient? client, MessageEnvelope envelope)
    {
        if (client is null || envelope.CorrelationId is not { } id)
            return;

        client.Touch();
        if (client.Pending.TryRemove(id, out var tcs))
        {
            var result = envelope.Unwrap<ToolResultMessage>()
                ?? new ToolResultMessage { ClientId = client.ClientId, IsError = true, Error = "empty result" };
            tcs.TrySetResult(result);
        }
    }

    private void HandleClientDown(LiveClient? client, MessageEnvelope envelope)
    {
        if (client is null)
            return;
        var msg = envelope.Unwrap<ClientDownMessage>();
        Disconnect(client, graceful: msg?.Graceful ?? true, reason: msg?.Reason ?? "client said goodbye");
    }

    private void Touch(LiveClient? client) => client?.Touch();

    // ===================================================================== disconnect

    private void Disconnect(LiveClient client, bool graceful, string? reason)
    {
        ClientInfo info;
        bool wasActive;
        lock (_gate)
        {
            if (!_live.TryGetValue(client.ClientId, out var current) || !ReferenceEquals(current, client))
                return; // already replaced/removed

            _live.Remove(client.ClientId);
            info = Snapshot_NoLock(client) with { IsConnected = false, LastSeenUtc = DateTimeOffset.UtcNow };
            _seen[client.ClientId] = info; // keep it visible for restart
            // Release the reserved suffix so a restart of THIS app re-takes the same
            // slot (NextFreeSuffix + ReserveSuffix both then land on the freed number).
            // The hub-id stays in _seen so its history/profile is still listable.
            //
            // MUST use the same key the reservation was made under. Releasing a remote client
            // under its bare AppId missed its `AppId@Host` bucket entirely, so the slot leaked
            // and the id climbed #1, #2, #3... on every reconnect — breaking the operator's
            // selection on every wifi blip and growing _seen forever. It also removed a slot
            // from an unrelated LOCAL app that happened to share the AppId.
            ReleaseSuffix_NoLock(client.IdentityKey, client.ClientId);
            wasActive = _default == client.ClientId;
            if (wasActive)
            {
                // Clear active so ListClients reflects reality, but remember this id so
                // its reconnect auto-reclaims active (finding-1 auto-reselect). A manual
                // hub_select_client of a different client now sets _default non-null, which
                // the reconnect's "_default is null" guard then respects.
                _default = null;
                _lastDefault = client.ClientId;
            }
        }

        // Fail any in-flight invokes so callers don't hang.
        foreach (var kv in client.Pending)
        {
            if (client.Pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new IOException($"Client '{client.ClientId}' disconnected: {reason}"));
        }

        ClientDown?.Invoke(this, info);
        if (wasActive)
            ClientUpdated?.Invoke(this, info); // active client vanished -> MCP re-lists (now empty)
    }

    // ===================================================================== watchdog

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.WatchdogInterval, ct).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                List<LiveClient> stale;
                lock (_gate)
                {
                    stale = _live.Values
                        .Where(c => now - c.LastSeenUtc > _options.HeartbeatTimeout)
                        .ToList();

                    // Forget launches that never registered. Left alone they would keep
                    // matching on process id, so an unrelated process that later reuses that
                    // pid would be handed an agent's claim.
                    foreach (var expired in _pendingLaunches.Values
                                 .Where(p => now - p.CreatedUtc > _options.LaunchRegisterTimeout)
                                 .ToList())
                    {
                        _pendingLaunches.Remove(expired.LaunchId);
                    }
                }

                foreach (var c in stale)
                {
                    try { c.Channel.Dispose(); } catch { /* ignore */ }
                    Disconnect(c, graceful: false, reason: "heartbeat timeout");
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    // ===================================================================== IClientBroker

    /// <inheritdoc/>
    public IReadOnlyList<ClientInfo> ListClients()
    {
        lock (_gate)
            return _live.Values.Select(Snapshot_NoLock).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<ClientInfo> ListKnownClients()
    {
        lock (_gate)
        {
            // Live first, then disconnected-but-seen (which includes persisted apps).
            var result = _live.Values.Select(Snapshot_NoLock).ToList();
            result.AddRange(_seen.Values);
            return result;
        }
    }

    /// <inheritdoc/>
    public ClientInfo? ClientStatus(string clientId)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(clientId, out var live))
                return Snapshot_NoLock(live);
            return _seen.TryGetValue(clientId, out var seen) ? seen : null;
        }
    }

    /// <inheritdoc/>
    public string? DefaultClientId
    {
        get { lock (_gate) return _default; }
        set
        {
            ClientInfo? info;
            lock (_gate)
            {
                _default = value;
                // A deliberate selection supersedes any pending auto-reselect target, so a
                // previously-active client reconnecting later won't steal this choice back.
                _lastDefault = null;
                info = value is not null
                    ? (_live.TryGetValue(value, out var l) ? Snapshot_NoLock(l)
                        : _seen.TryGetValue(value, out var s) ? s : null)
                    : null;
            }
            // Raise even when info is null so the MCP server re-lists to an empty catalog.
            ClientUpdated?.Invoke(this, info ?? new ClientInfo { ClientId = value ?? string.Empty, IsConnected = false });
        }
    }

    /// <inheritdoc/>
    public async Task<ToolResultMessage> InvokeOnClientAsync(
        string clientId, string toolName, JsonElement? argumentsJson,
        CancellationToken cancellationToken = default, string? agent = null)
    {
        LiveClient client;
        lock (_gate)
        {
            if (!_live.TryGetValue(clientId, out var c))
                throw new InvalidOperationException($"Client '{clientId}' is not connected.");
            client = c;
        }

        if (client.ReadOnly && !IsReadOnlyTool(client, toolName))
            throw new InvalidOperationException($"Client '{clientId}' is read-only; tool '{toolName}' is refused.");

        _audit.Add(new AuditEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ClientId = clientId,
            ToolName = toolName,
            Agent = agent,
            Outcome = AuditOutcome.Started,
        });

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Pending[correlationId] = tcs;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_options.InvokeTimeout);

        try
        {
            // linked.Token, NOT the caller's: the timeout has to cover the SEND as well as the
            // wait for a reply. A client that roams out of range leaves the hub's socket buffer
            // full, so the write blocks while holding the channel's write lock, and a tool call
            // queued behind it waits forever — the watchdog eventually disposes the channel,
            // and disposing a SemaphoreSlim does not release anyone already parked on it. The
            // 60s InvokeTimeout exists to prevent exactly this and previously never applied
            // until after the send had returned.
            await client.Channel.SendAsync(MessageKind.InvokeTool, new InvokeToolMessage
            {
                ClientId = client.AppId, // the app identifies itself by its self-reported id
                ToolName = toolName,
                Arguments = argumentsJson,
            }, correlationId, linked.Token).ConfigureAwait(false);

            using (linked.Token.Register(static state =>
                ((TaskCompletionSource<ToolResultMessage>)state!).TrySetCanceled(), tcs))
            {
                var result = await tcs.Task.ConfigureAwait(false);
                _audit.Add(new AuditEntry
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    ClientId = clientId,
                    ToolName = toolName,
                    Agent = agent,
                    Outcome = result.IsError ? AuditOutcome.Error : AuditOutcome.Ok,
                    Error = result.IsError ? result.Error : null,
                });
                return result;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            client.Pending.TryRemove(correlationId, out _);
            _audit.Add(new AuditEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                ClientId = clientId,
                ToolName = toolName,
                Agent = agent,
                Outcome = AuditOutcome.Error,
                Error = "timed out",
            });
            throw new TimeoutException($"Tool '{toolName}' on '{clientId}' did not respond within {_options.InvokeTimeout}.");
        }
        catch (Exception ex)
        {
            client.Pending.TryRemove(correlationId, out _);
            _audit.Add(new AuditEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                ClientId = clientId,
                ToolName = toolName,
                Agent = agent,
                Outcome = AuditOutcome.Error,
                Error = ex.Message,
            });
            throw;
        }
    }

    /// <summary>
    /// Refuses to start a process for a client that is not on this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the most important guard in the remote work, because the failure it prevents is
    /// <i>silent and plausible</i>: <c>myapp@MACHINENAME#1</c> strips to
    /// <c>myapp@MACHINENAME</c>, which misses in the launch-profile store and would then
    /// fall back to a second lookup that could resolve the LOCAL <c>myapp</c> profile.
    /// The hub would start a local copy, report a process id, and the operator would believe
    /// they had restarted the remote machine.
    /// </para>
    /// <para>
    /// It checks the recorded <see cref="ClientInfo.CanLaunch"/> — a fact established by the
    /// listener from the transport — rather than re-deriving remoteness by inspecting the id,
    /// so it cannot be defeated by a client picking an app id with an <c>@</c> in it.
    /// </para>
    /// </remarks>
    private void EnsureLaunchable(string clientId)
    {
        ClientInfo? known;
        lock (_gate)
        {
            known = _live.TryGetValue(clientId, out var live)
                ? Snapshot_NoLock(live)
                : _seen.GetValueOrDefault(clientId);

            // An exact miss is NOT a free pass. `hub_launch_client { clientId: "myapp" }`
            // — the bare form the guide and the wait-filters actively encourage — matches
            // neither dictionary, so the guard used to fall straight through to
            // ResolveProfile("myapp"), find the LOCAL launch profile, and start a local
            // copy while the only connected 'myapp' was the remote one. That is precisely
            // the silent-and-plausible failure this guard exists to prevent, reached by the
            // most natural spelling of the request. So an id that resolves ONLY to remote
            // clients is refused too.
            if (known is null)
            {
                // Match the bare app id AND the composite `AppId@Host` — the spelling the guide
                // teaches for disambiguating one app across machines. It misses both dictionaries
                // (they are keyed with the `#n` suffix) and it is not the bare AppId either, so
                // matching only on AppId left this spelling as the one way into the launch path
                // with the guard skipped. The operator then got "has no recorded executable path
                // to launch" — which reads as "record a path" when the truth is "that process is
                // on another machine".
                var candidates = _live.Values
                    .Where(c => string.Equals(c.AppId, clientId, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(c.IdentityKey, clientId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count > 0 && candidates.TrueForAll(c => !c.CanLaunch))
                    known = Snapshot_NoLock(candidates[0]);
            }
        }

        if (known is null || known.CanLaunch)
            return;

        var where = known.Host is { Length: > 0 } host ? $"on {host}" : "on another machine";
        throw new InvalidOperationException(
            $"'{clientId}' is a remote client; the hub cannot start or stop processes {where}. " +
            "Start it from that machine, or use its own deployment/update mechanism.");
    }

    /// <inheritdoc/>
    public Task<LaunchResult> LaunchClientAsync(
        string clientId, LaunchOptions? launch = null, CancellationToken cancellationToken = default)
    {
        EnsureLaunchable(clientId);

        var profile = ResolveLaunchProfile(clientId, launch);
        return Task.FromResult(StartTrackedProcess(clientId, profile, launch));
    }

    /// <summary>
    /// Works out what to actually start: the recorded profile, with an agent's overrides
    /// applied on top and sanity-checked.
    /// </summary>
    /// <remarks>
    /// The guard on <see cref="LaunchOptions.ExePath"/> is a footgun guard, not a security
    /// boundary — the MCP surface already carries this user's authority, and the hub already
    /// starts whatever the persisted profile names. What it prevents is an agent quietly
    /// launching a <i>different application</i> under the name of one the operator knows: the
    /// worktree case is the same executable in another directory, so a different file name
    /// means something has gone wrong unless the caller says otherwise.
    /// </remarks>
    private KnownClientProfile ResolveLaunchProfile(string clientId, LaunchOptions? launch)
    {
        var recorded = ResolveProfile(clientId);

        if (launch?.ExePath is not { Length: > 0 } overridePath)
        {
            if (recorded is null)
                throw new InvalidOperationException($"No launch profile recorded for '{clientId}'.");
            if (string.IsNullOrEmpty(recorded.ExecutablePath))
                throw new InvalidOperationException($"'{clientId}' has no recorded executable path to launch.");
            return recorded;
        }

        var fullPath = Path.GetFullPath(overridePath);
        if (!File.Exists(fullPath))
            throw new InvalidOperationException($"'{fullPath}' does not exist, so there is nothing to launch.");

        if (!launch.AllowDifferentExecutable
            && recorded?.ExecutablePath is { Length: > 0 } known
            && !string.Equals(
                Path.GetFileName(known), Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{clientId}' is recorded as '{Path.GetFileName(known)}' but you asked to launch "
                + $"'{Path.GetFileName(fullPath)}'. Launching a different build of the same app from "
                + "another directory is expected (that is the worktree case) — a different file name "
                + "usually is not. Pass allowDifferentExecutable=true if you really meant it.");
        }

        return new KnownClientProfile
        {
            AppId = recorded?.AppId ?? StripSuffix(clientId),
            Host = recorded?.Host,
            DisplayName = recorded?.DisplayName,
            ExecutablePath = fullPath,
            Arguments = launch.Arguments ?? recorded?.Arguments,
            WorkingDirectory = launch.WorkingDirectory ?? Path.GetDirectoryName(fullPath),
            ReadOnly = recorded?.ReadOnly ?? false,
            LastSeenUtc = recorded?.LastSeenUtc ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Starts a process and remembers that we did, so the registration it produces can be
    /// handed back to the agent that asked for it.
    /// </summary>
    private LaunchResult StartTrackedProcess(
        string clientId, KnownClientProfile profile, LaunchOptions? launch)
    {
        var launchId = Guid.NewGuid().ToString("N");
        var identity = IdentityOf(StripSuffix(clientId), profile.Host);

        var pid = StartProcess(profile, launchId);

        lock (_gate)
        {
            _pendingLaunches[launchId] = new PendingLaunch
            {
                LaunchId = launchId,
                Identity = identity,
                ProcessId = pid,
                SessionId = launch?.SessionId,
                SessionLabel = launch?.SessionLabel,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
        }

        _audit.Add(new AuditEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ClientId = clientId,
            ToolName = $"launched '{profile.ExecutablePath}' (pid {pid})",
            Agent = launch?.SessionLabel,
            Kind = AuditKind.Launch,
            Outcome = AuditOutcome.Ok,
        });

        return new LaunchResult(pid, launchId);
    }

    /// <inheritdoc/>
    public async Task<LaunchResult> RestartClientAsync(
        string clientId, LaunchOptions? launch = null, CancellationToken cancellationToken = default)
    {
        EnsureLaunchable(clientId);

        // A bare app id is ambiguous the moment more than one instance is up — which is the
        // normal state once several agents each run their own worktree build. Silently
        // starting yet another copy (the old behaviour) is the worst answer: the agent
        // believes it restarted the app it was driving and is now talking to a third one.
        var targetId = ResolveRestartTarget(clientId);

        // Capture the live session (if any) so we can terminate the running instance, while
        // keeping the reserved hub-id so the relaunched app re-takes the same slot.
        LiveClient? liveClient = null;
        lock (_gate)
        {
            if (targetId is not null && _live.TryGetValue(targetId, out var live))
                liveClient = live;
        }

        var profile = ResolveLaunchProfile(targetId ?? clientId, launch);

        if (liveClient?.ProcessId is > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(liveClient.ProcessId);
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { /* already exited */ }
        }

        // Retire the old session from the registry NOW rather than waiting for its receive
        // loop to notice the transport closed. Otherwise the instance we just killed is still
        // listed as connected for a moment, and the caller's very next
        // hub_wait_for_client — the natural way to wait for the app to come back — resolves
        // against the corpse and reports success before the replacement has even started.
        if (liveClient is not null)
        {
            try { liveClient.Channel.Dispose(); } catch { /* already dead */ }
            Disconnect(liveClient, graceful: false, reason: "restarted by the hub");
        }

        // Relaunch through the tracked path so the new process — which has a new pid — is
        // re-associated with the same agent, and its claim (keyed on the hub id, which the
        // reconnect reclaims) carries straight over.
        return StartTrackedProcess(targetId ?? clientId, profile, launch);
    }

    /// <summary>
    /// Resolves which instance a restart means. An exact hub id is taken at face value; a
    /// bare app id resolves only when it is unambiguous.
    /// </summary>
    private string? ResolveRestartTarget(string clientId)
    {
        lock (_gate)
        {
            if (_live.ContainsKey(clientId) || _seen.ContainsKey(clientId))
                return clientId;

            var candidates = _live.Values
                .Where(c => string.Equals(c.AppId, clientId, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(c.IdentityKey, clientId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
                return null; // nothing running: treat as a plain launch
            if (candidates.Count == 1)
                return candidates[0].ClientId;

            throw new InvalidOperationException(
                $"'{clientId}' matches {candidates.Count} running instances "
                + $"({string.Join(", ", candidates.Select(c => c.ClientId))}). "
                + "Name the one you mean — restarting the wrong instance would interrupt "
                + "whichever agent is driving it.");
        }
    }

    /// <inheritdoc/>
    public Task<ClientInfo?> WaitForClientAsync(
        string? appIdOrClientId, TimeSpan timeout, CancellationToken cancellationToken = default)
        => WaitForClientAsync(
            new ClientWaitFilter { IdOrAppId = appIdOrClientId }, timeout, cancellationToken);

    /// <inheritdoc/>
    public async Task<ClientInfo?> WaitForClientAsync(
        ClientWaitFilter filter, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Fast path: already connected? Return without awaiting any event. Doing this
        // BEFORE subscribing closes the race where the client connects between the check
        // and the subscription (event-driven brokers' classic lost-wakeup).
        if (FindConnectedMatch(filter) is { } already)
            return already;

        var tcs = new TaskCompletionSource<ClientInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnConnected(object? _, ClientInfo info)
        {
            if (Matches(info, filter, ClaimOwnerOf(info.ClientId)))
                tcs.TrySetResult(info);
        }

        ClientConnected += OnConnected;
        try
        {
            // Re-check after subscribing: a connect that landed in the tiny window between
            // the fast-path check and the subscription is recovered here.
            if (FindConnectedMatch(filter) is { } raced)
                return raced;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using (timeoutCts.Token.Register(static s =>
                       ((TaskCompletionSource<ClientInfo>)s!).TrySetCanceled(), tcs))
            {
                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null; // timed out — no matching client appeared in time
                }
            }
        }
        finally
        {
            ClientConnected -= OnConnected;
        }
    }

    /// <summary>
    /// The best connected client matching the filter, or null.
    /// </summary>
    /// <remarks>
    /// The ordering is the point, and it is documented on <c>hub_wait_for_client</c>: an exact
    /// hub-id match first, then an instance the asking agent already owns, then one nobody
    /// owns, then the rest — each group by ascending instance number. Enumeration order used
    /// to decide this, which meant a bare app id resolved to an arbitrary instance; with
    /// several agents each running their own build, arbitrary means "sometimes somebody
    /// else's app, silently".
    /// </remarks>
    private ClientInfo? FindConnectedMatch(ClientWaitFilter filter)
    {
        lock (_gate)
        {
            var matches = new List<(ClientInfo info, int rank)>();
            foreach (var c in _live.Values)
            {
                var info = Snapshot_NoLock(c);
                var owner = ClaimOwnerOf(info.ClientId);
                if (!Matches(info, filter, owner))
                    continue;

                var rank =
                    string.Equals(info.ClientId, filter.IdOrAppId, StringComparison.OrdinalIgnoreCase) ? 0
                    : IsMine(info, owner, filter.SessionId) ? 1
                    : owner is null ? 2
                    : 3;
                matches.Add((info, rank));
            }

            return matches
                .OrderBy(m => m.rank)
                .ThenBy(m => m.info.ClientId, StringComparer.OrdinalIgnoreCase)
                .Select(m => m.info)
                .FirstOrDefault();
        }
    }

    /// <summary>The agent driving a client, when the hub is tracking claims at all.</summary>
    private ClaimInfo? ClaimOwnerOf(string clientId) => Claims?.Get(clientId);

    /// <summary>
    /// Whether an instance belongs to the asking agent — either it is driving it, or it asked
    /// the hub to launch it.
    /// </summary>
    private static bool IsMine(ClientInfo info, ClaimInfo? owner, Guid? sessionId) =>
        sessionId is { } id && (owner?.SessionId == id || info.LaunchSessionId == id);

    /// <summary>
    /// The claim registry, when one has been attached. Null in hosts that never wired one up
    /// (older tests), in which case every instance simply reads as unclaimed.
    /// </summary>
    public ClientClaimRegistry? Claims { get; set; }

    /// <summary>
    /// Whether a client snapshot satisfies a wait filter. A null/empty filter matches any
    /// connected client; otherwise the filter must equal the hub id (<c>myapp@MACHINENAME#1</c>),
    /// the bare app id (<c>myapp</c>), or the app-and-host (<c>myapp@MACHINENAME</c>).
    /// </summary>
    /// <remarks>
    /// The app-and-host form matters because the bare app id deliberately still matches a
    /// remote client — <c>hub_wait_for_client { appId: "myapp" }</c> should find the remote machine —
    /// but with a local instance also running it would be a coin toss which one resolves.
    /// The middle form is how a caller says which they meant without pinning an instance number.
    /// </remarks>
    private static bool Matches(ClientInfo info, ClientWaitFilter filter, ClaimInfo? owner)
    {
        if (!info.IsConnected)
            return false;

        // A launch id names exactly one instance, so it answers on its own — that is the
        // whole point of handing one back from hub_launch_client.
        if (filter.LaunchId is { Length: > 0 } launchId)
            return string.Equals(info.LaunchId, launchId, StringComparison.Ordinal);

        if (filter.ProcessId is { } pid && info.ProcessId != pid)
            return false;

        if (filter.UnclaimedOnly && owner is not null)
            return false;

        if (filter.MineOnly && !IsMine(info, owner, filter.SessionId))
            return false;

        return MatchesId(info, filter.IdOrAppId);
    }

    private static bool MatchesId(ClientInfo info, string? appIdOrClientId)
    {
        if (string.IsNullOrWhiteSpace(appIdOrClientId))
            return true;

        if (string.Equals(info.ClientId, appIdOrClientId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(info.AppId, appIdOrClientId, StringComparison.OrdinalIgnoreCase))
            return true;

        return info.Host is { Length: > 0 } host
            && string.Equals($"{info.AppId}@{host}", appIdOrClientId, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientConnected;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientUpdated;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientDown;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? LaunchRegistered;

    // ===================================================================== read-only toggle

    /// <summary>
    /// Sets the read-only flag for a client (tray toggle) and raises <see cref="ClientUpdated"/>.
    /// </summary>
    /// <remarks>
    /// The setting is persisted against the client's full identity — the bare app id for a
    /// local app, <c>AppId@Host</c> for a remote one — so it survives both a reconnect and a
    /// hub restart. Keying on the full identity is what keeps the two separate: lifting
    /// read-only on the remote machine must not quietly make the copy of the same app on this desk
    /// writable, and vice versa.
    /// <para>
    /// Remote clients still <i>start</i> read-only; what is remembered is the operator's
    /// decision once they have made one. On a link that drops as often as a mobile device's, having
    /// to re-authorise after every blip made the permission meaningless in practice.
    /// </para>
    /// </remarks>
    public void SetReadOnly(string clientId, bool readOnly, string? agent = null)
    {
        ClientInfo? info = null;
        string? identity = null;
        lock (_gate)
        {
            if (_live.TryGetValue(clientId, out var live))
            {
                live.ReadOnly = readOnly;
                identity = live.IdentityKey;
                info = Snapshot_NoLock(live);
            }
            else if (_seen.TryGetValue(clientId, out var seen))
            {
                identity = IdentityOf(seen.AppId ?? clientId, seen.Host);
                info = seen with { ReadOnly = readOnly };
                _seen[clientId] = info;
            }
        }

        if (identity is not null)
            _store.SetReadOnly(identity, readOnly);
        if (info is not null)
        {
            _audit.Add(Remote.RemoteAudit.Entry(
                AuditKind.Escalate,
                readOnly ? $"'{clientId}' set read-only" : $"'{clientId}' allowed to accept mutating tools",
                clientId: clientId, host: info.Host, transport: info.Transport) with { Agent = agent });
            ClientUpdated?.Invoke(this, info);
        }
    }

    // ===================================================================== id/slot helpers

    // Reserves (or returns the existing reservation for) a hub-id for an app with the
    // given preferred suffix. Slots persist for the broker's lifetime so a disconnected
    // client's id is re-taken by its restart instead of drifting.
    /// <summary>
    /// Records <paramref name="n"/> as taken for <paramref name="identity"/> and returns the
    /// hub id, without searching for a different free number.
    /// </summary>
    /// <remarks>
    /// For the registration path, where the caller has already established that no <i>live</i>
    /// client holds the slot. A reservation left behind by a disconnected client is exactly
    /// what a reconnecting app is supposed to reclaim.
    /// </remarks>
    private string ClaimSuffix(string identity, int n)
    {
        lock (_gate)
        {
            if (!_reservedSuffixes.TryGetValue(identity, out var set))
            {
                set = new SortedSet<int>();
                _reservedSuffixes[identity] = set;
            }
            set.Add(n);
            return FormatId(identity, n);
        }
    }

    private string ReserveSuffix(string appId, int preferred)
    {
        lock (_gate)
        {
            if (!_reservedSuffixes.TryGetValue(appId, out var set))
            {
                set = new SortedSet<int>();
                _reservedSuffixes[appId] = set;
            }
            var n = preferred;
            while (set.Contains(n))
                n++;
            set.Add(n);
            return FormatId(appId, n);
        }
    }

    // Releases the reserved suffix carried by a hub-id (caller holds _gate). Called on
    // disconnect so a restart of the same app re-takes the freed slot/id. Best-effort:
    // a hub-id without a parseable suffix simply has nothing to release.
    private void ReleaseSuffix_NoLock(string appId, string hubId)
    {
        if (!_reservedSuffixes.TryGetValue(appId, out var set))
            return;
        var hash = hubId.LastIndexOf('#');
        if (hash >= 0 && int.TryParse(hubId.AsSpan(hash + 1), out var n))
            set.Remove(n);
        else if (!_options.AlwaysSuffixInstance)
            set.Remove(1); // bare-AppId form maps to slot #1
    }

    // The lowest suffix for this app that has no LIVE client right now (re-uses a freed
    // slot left by a disconnect so a restart lands on the same id).
    private int NextFreeSuffix(string appId)
    {
        lock (_gate)
        {
            for (var n = 1; ; n++)
            {
                var id = FormatId(appId, n);
                if (!_live.ContainsKey(id))
                    return n;
            }
        }
    }

    private string FormatId(string appId, int suffix)
        => _options.AlwaysSuffixInstance ? $"{appId}#{suffix}" : (suffix == 1 ? appId : $"{appId}#{suffix}");

    private KnownClientProfile? ResolveProfile(string clientId)
    {
        // clientId may be a hub-id (AppId#n) or a bare AppId; map to the persisted profile.
        var appId = StripSuffix(clientId);
        return _store.Get(appId) ?? _store.Get(clientId);
    }

    private static string StripSuffix(string hubId)
    {
        var hash = hubId.LastIndexOf('#');
        return hash > 0 ? hubId[..hash] : hubId;
    }

    private ClientInfo Snapshot_NoLock(LiveClient c) => new()
    {
        ClientId = c.ClientId,
        AppId = c.AppId,
        DisplayName = c.DisplayName,
        ProcessId = c.ProcessId,
        IsConnected = true,
        OwnsWindows = c.OwnsWindows,
        ClientVersion = c.ClientVersion,
        ReadOnly = c.ReadOnly,
        Tools = c.Tools,
        ExecutablePath = c.ExecutablePath,
        LastSeenUtc = c.LastSeenUtc,
        Transport = c.Transport,
        Host = c.Host,
        MachineId = c.MachineId,
        CanLaunch = c.CanLaunch,
        LaunchId = c.LaunchId,
        LaunchSessionId = c.LaunchSessionId,
        LaunchSessionLabel = c.LaunchSessionLabel,
    };

    /// <summary>The longest app id the hub will accept; longer ids are truncated.</summary>
    public const int MaxAppIdLength = 64;

    /// <summary>
    /// Coerces a client-reported app id into something safe to use as a registry key and as
    /// part of a hub id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The app id is chosen entirely by the client — it is the one identity field the hub does
    /// <b>not</b> derive from the connection — yet it ends up in <c>_live</c>/<c>_seen</c>/
    /// <c>_reservedSuffixes</c> keys, in the hub id, and in the filters
    /// <c>hub_wait_for_client</c> matches on. Sanitising rather than rejecting keeps every
    /// existing local app working (assembly names are already within this charset) while
    /// removing three problems:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>@</c> and <c>#</c> are structural in <c>AppId@Host#n</c>. A remote client
    ///   registering as <c>myapp@MACHINENAME</c> would have its <c>AppId</c> compare equal
    ///   to the app-and-host disambiguator, so <c>hub_wait_for_client</c> would hand the
    ///   operator that client instead of the real one — and subsequent tool calls, arguments
    ///   included, would go to it. The host half is unspoofable (it comes from the validated
    ///   certificate); this closes the other half.</item>
    ///   <item>An unbounded id is a free way to bloat every dictionary keyed on it.</item>
    ///   <item>Control characters and whitespace corrupt the tray list and the audit trail.</item>
    /// </list>
    /// </remarks>
    internal static string SanitizeAppId(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported))
            return "avalonia-app";

        var sb = new System.Text.StringBuilder(Math.Min(reported.Length, MaxAppIdLength));
        foreach (var c in reported.Trim())
        {
            if (sb.Length >= MaxAppIdLength)
                break;
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                or '.' or '-' or '_' or '+';
            sb.Append(ok ? c : '_');
        }

        var result = sb.ToString().Trim('.', '-');
        return result.Length == 0 ? "avalonia-app" : result;
    }

    /// <summary>
    /// Whether a read-only client may still run <paramref name="toolName"/>. The rule lives
    /// in <see cref="ToolClassification"/> so this gate and the hub's write-claim gate can
    /// never drift apart about what counts as a mutating call.
    /// </summary>
    private static bool IsReadOnlyTool(LiveClient client, string toolName)
        => ToolClassification.IsReadOnly(client.Tools, toolName);

    private static (string? path, string? args, string? cwd) ResolveProcessProfile(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var path = proc.MainModule?.FileName;
            string? cwd = null;
            try { cwd = Path.GetDirectoryName(path); } catch { /* ignore */ }
            return (path, null, cwd);
        }
        catch
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Starts the app, tagging it with <paramref name="launchId"/> so the process can identify
    /// which launch it came from when it registers.
    /// </summary>
    /// <remarks>
    /// Passing an environment variable requires <c>UseShellExecute=false</c>, which cannot
    /// start non-executable targets (a document, a shortcut). Recorded profiles are real
    /// executables in practice, but rather than assume it, a failure falls back to the old
    /// shell-execute launch without the token — the launch still works, and correlation falls
    /// back to matching on process id.
    /// </remarks>
    private int StartProcess(KnownClientProfile profile, string? launchId = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = profile.ExecutablePath!,
            UseShellExecute = launchId is null,
        };
        if (!string.IsNullOrEmpty(profile.Arguments))
            psi.Arguments = profile.Arguments;
        if (!string.IsNullOrEmpty(profile.WorkingDirectory) && Directory.Exists(profile.WorkingDirectory))
            psi.WorkingDirectory = profile.WorkingDirectory;

        if (launchId is null)
            return Start(psi);

        psi.Environment[PipeNames.LaunchTokenEnvVar] = launchId;
        try
        {
            return Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            var shell = new ProcessStartInfo
            {
                FileName = psi.FileName,
                Arguments = psi.Arguments,
                WorkingDirectory = psi.WorkingDirectory,
                UseShellExecute = true,
            };
            return Start(shell);
        }
    }

    /// <summary>Starts a process through the configured starter (the test seam), or the OS.</summary>
    private int Start(ProcessStartInfo psi)
    {
        if (_options.ProcessStarter is { } custom)
            return custom(psi);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{psi.FileName}'.");
        return proc.Id;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        try { if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        try { if (_watchdog is not null) await _watchdog.ConfigureAwait(false); } catch { /* ignore */ }

        List<LiveClient> live;
        lock (_gate)
        {
            live = _live.Values.ToList();
            _live.Clear();
        }
        foreach (var c in live)
        {
            try { await c.Channel.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }

    // ===================================================================== nested

    /// <summary>
    /// A process the hub started on an agent's behalf, waiting to be matched to the
    /// registration it produces.
    /// </summary>
    private sealed class PendingLaunch
    {
        public required string LaunchId { get; init; }

        /// <summary>The app identity this launch was for, so a pid match cannot cross apps.</summary>
        public required string Identity { get; init; }

        /// <summary>The pid the hub started — the fallback match for clients with no token.</summary>
        public required int ProcessId { get; init; }

        /// <summary>The agent that asked for it, and will be given the instance.</summary>
        public Guid? SessionId { get; init; }

        public string? SessionLabel { get; init; }

        public required DateTimeOffset CreatedUtc { get; init; }
    }

    /// <summary>A live pipe session for one connected client.</summary>
    private sealed class LiveClient
    {
        public LiveClient(string clientId, string appId, PipeChannel channel)
        {
            ClientId = clientId;
            AppId = appId;
            Channel = channel;
        }

        public string ClientId { get; }
        public string AppId { get; }
        public PipeChannel Channel { get; }

        public string? DisplayName { get; set; }
        public int ProcessId { get; set; }
        public string? ExecutablePath { get; set; }
        public string? Arguments { get; set; }
        public string? WorkingDirectory { get; set; }
        public bool ReadOnly { get; set; }
        public bool OwnsWindows { get; set; }
        public string? ClientVersion { get; set; }
        public DateTimeOffset ConnectedAtUtc { get; set; }
        public IReadOnlyList<ToolDescriptor> Tools { get; set; } = Array.Empty<ToolDescriptor>();

        public ClientTransport Transport { get; set; } = ClientTransport.Pipe;
        public string? Host { get; set; }
        public string? MachineId { get; set; }
        public bool CanLaunch { get; set; } = true;
        public bool IsRemote => Transport != ClientTransport.Pipe;

        /// <summary>
        /// The key this client's instance suffix was reserved under — <c>AppId</c> locally,
        /// <c>AppId@Host</c> for remote.
        /// </summary>
        /// <remarks>
        /// Stored rather than recomputed on release. Reserving under one key and releasing
        /// under another leaks the slot forever, so the id climbs <c>#1, #2, #3…</c> on every
        /// reconnect — which on a flaky link means the operator's selection breaks every time
        /// the machine blips, and <c>_seen</c> grows without bound.
        /// </remarks>
        public string IdentityKey { get; set; } = string.Empty;

        /// <summary>The launch this instance came from, when the hub started it for an agent.</summary>
        public string? LaunchId { get; set; }

        /// <summary>The agent session that asked for this instance.</summary>
        public Guid? LaunchSessionId { get; set; }

        /// <summary>That agent's display label.</summary>
        public string? LaunchSessionLabel { get; set; }

        // Correlation table: correlationId -> awaiting invoke.
        public ConcurrentDictionary<string, TaskCompletionSource<ToolResultMessage>> Pending { get; } = new();

        private long _lastSeenTicks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset LastSeenUtc => new(Volatile.Read(ref _lastSeenTicks), TimeSpan.Zero);
        public void Touch() => Volatile.Write(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);
    }
}
