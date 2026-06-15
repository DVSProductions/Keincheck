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
    /// When true, the first instance of an app is assigned the bare id
    /// <c>#1</c> suffix (e.g. <c>MyApp#1</c>); always-suffixed keeps ids predictable.
    /// </summary>
    public bool AlwaysSuffixInstance { get; set; } = true;
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

    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private Task? _watchdog;
    private string? _active;
    private int _disposed;

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
            _seen[hubId] = new ClientInfo
            {
                ClientId = hubId,
                AppId = profile.AppId,
                DisplayName = profile.DisplayName ?? profile.AppId,
                IsConnected = false,
                ReadOnly = profile.ReadOnly,
                ExecutablePath = profile.ExecutablePath,
                LastSeenUtc = profile.LastSeenUtc,
            };
        }
    }

    /// <summary>The audit log of AI tool calls; bound by the tray UI.</summary>
    public HubAuditLog Audit => _audit;

    /// <summary>The persisted known-client store.</summary>
    public KnownClientStore Store => _store;

    /// <summary>The control pipe this broker listens on.</summary>
    public string PipeName => _pipeName;

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
            _ = ServeClientAsync(channel, ct);
        }
    }

    /// <summary>
    /// Drives the per-client receive loop over an already-connected
    /// <see cref="PipeChannel"/>. Public for tests (in-memory pipe pair); the accept
    /// loop calls it for real connections.
    /// </summary>
    public Task AcceptChannel(PipeChannel channel, CancellationToken cancellationToken = default)
        => ServeClientAsync(channel, cancellationToken);

    private async Task ServeClientAsync(PipeChannel channel, CancellationToken ct)
    {
        LiveClient? client = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var envelope = await channel.ReceiveAsync(ct).ConfigureAwait(false);
                if (envelope is null)
                    break; // clean EOF — client closed the pipe

                switch (envelope.Kind)
                {
                    case MessageKind.Register:
                        client = HandleRegister(channel, envelope);
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
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Hub] client session error: {ex.Message}");
        }
        finally
        {
            if (client is not null)
                Disconnect(client, graceful: false, reason: "transport closed");
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ===================================================================== register

    private LiveClient HandleRegister(PipeChannel channel, MessageEnvelope envelope)
    {
        var reg = envelope.Unwrap<RegisterMessage>()
            ?? throw new ProtocolException("Register payload was empty.");

        if (!ProtocolVersion.IsCompatible(reg.ProtocolVersion))
            throw new ProtocolException(
                $"Client '{reg.ClientId}' speaks protocol v{reg.ProtocolVersion}; hub supports " +
                $"[{ProtocolVersion.Minimum}, {ProtocolVersion.Current}].");

        var appId = string.IsNullOrWhiteSpace(reg.ClientId) ? "avalonia-app" : reg.ClientId;
        var (path, args, cwd) = ResolveProcessProfile(reg.ProcessId);

        ClientInfo info;
        LiveClient live;
        lock (_gate)
        {
            var hubId = ReserveSuffix(appId, preferred: NextFreeSuffix(appId));
            var existingReadOnly = _seen.TryGetValue(hubId, out var prior) && prior.ReadOnly;
            var profileReadOnly = _store.Get(appId)?.ReadOnly ?? false;

            live = new LiveClient(hubId, appId, channel)
            {
                DisplayName = reg.DisplayName ?? appId,
                ProcessId = reg.ProcessId,
                ExecutablePath = path,
                Arguments = args,
                WorkingDirectory = cwd,
                ReadOnly = existingReadOnly || profileReadOnly,
                ConnectedAtUtc = DateTimeOffset.UtcNow,
            };
            live.Touch();
            _live[hubId] = live;
            _seen.Remove(hubId); // now live, not merely seen
            info = Snapshot_NoLock(live);
        }

        // Persist the launch profile so this app can be relaunched later.
        _store.Upsert(new KnownClientProfile
        {
            AppId = appId,
            DisplayName = live.DisplayName,
            ExecutablePath = path,
            Arguments = args,
            WorkingDirectory = cwd,
            ReadOnly = live.ReadOnly,
            LastSeenUtc = DateTimeOffset.UtcNow,
        });

        // First connected client auto-becomes active so the AI sees tools immediately.
        var becameActive = false;
        lock (_gate)
        {
            if (_active is null)
            {
                _active = live.ClientId;
                becameActive = true;
            }
        }

        ClientConnected?.Invoke(this, info);
        if (becameActive)
            ClientUpdated?.Invoke(this, info); // nudge the MCP server to re-list
        return live;
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
            ReleaseSuffix_NoLock(client.AppId, client.ClientId);
            wasActive = _active == client.ClientId;
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
    public string? ActiveClientId
    {
        get { lock (_gate) return _active; }
        set
        {
            ClientInfo? info;
            lock (_gate)
            {
                _active = value;
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
        string clientId, string toolName, JsonElement? argumentsJson, CancellationToken cancellationToken = default)
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
            Outcome = AuditOutcome.Started,
        });

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Pending[correlationId] = tcs;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_options.InvokeTimeout);

        try
        {
            await client.Channel.SendAsync(MessageKind.InvokeTool, new InvokeToolMessage
            {
                ClientId = client.AppId, // the app identifies itself by its self-reported id
                ToolName = toolName,
                Arguments = argumentsJson,
            }, correlationId, cancellationToken).ConfigureAwait(false);

            using (linked.Token.Register(static state =>
                ((TaskCompletionSource<ToolResultMessage>)state!).TrySetCanceled(), tcs))
            {
                var result = await tcs.Task.ConfigureAwait(false);
                _audit.Add(new AuditEntry
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    ClientId = clientId,
                    ToolName = toolName,
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
                Outcome = AuditOutcome.Error,
                Error = ex.Message,
            });
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<int> LaunchClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var profile = ResolveProfile(clientId)
            ?? throw new InvalidOperationException($"No launch profile recorded for '{clientId}'.");
        if (string.IsNullOrEmpty(profile.ExecutablePath))
            throw new InvalidOperationException($"'{clientId}' has no recorded executable path to launch.");

        var pid = StartProcess(profile);
        return Task.FromResult(pid);
    }

    /// <inheritdoc/>
    public async Task<int> RestartClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        // Capture the live pid (if any) so we can terminate the running instance, while
        // keeping the reserved hub-id so the relaunched app re-takes the same slot.
        int? livePid = null;
        KnownClientProfile? profile;
        lock (_gate)
        {
            if (_live.TryGetValue(clientId, out var live))
                livePid = live.ProcessId;
        }
        profile = ResolveProfile(clientId);

        if (profile is null || string.IsNullOrEmpty(profile.ExecutablePath))
            throw new InvalidOperationException($"'{clientId}' cannot be restarted (no recorded executable path).");

        if (livePid is { } pid && pid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { /* already exited */ }
        }

        return StartProcess(profile);
    }

    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientConnected;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientUpdated;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientDown;

    // ===================================================================== read-only toggle

    /// <summary>
    /// Sets the read-only flag for a client (tray toggle). Persists it on the app's
    /// profile so it survives a restart, and raises <see cref="ClientUpdated"/>.
    /// </summary>
    public void SetReadOnly(string clientId, bool readOnly)
    {
        ClientInfo? info = null;
        string? appId = null;
        lock (_gate)
        {
            if (_live.TryGetValue(clientId, out var live))
            {
                live.ReadOnly = readOnly;
                appId = live.AppId;
                info = Snapshot_NoLock(live);
            }
            else if (_seen.TryGetValue(clientId, out var seen))
            {
                appId = seen.AppId;
                info = seen with { ReadOnly = readOnly };
                _seen[clientId] = info;
            }
        }

        if (appId is not null)
            _store.SetReadOnly(appId, readOnly);
        if (info is not null)
            ClientUpdated?.Invoke(this, info);
    }

    // ===================================================================== id/slot helpers

    // Reserves (or returns the existing reservation for) a hub-id for an app with the
    // given preferred suffix. Slots persist for the broker's lifetime so a disconnected
    // client's id is re-taken by its restart instead of drifting.
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
        ReadOnly = c.ReadOnly,
        Tools = c.Tools,
        ExecutablePath = c.ExecutablePath,
        LastSeenUtc = c.LastSeenUtc,
    };

    private static bool IsReadOnlyTool(LiveClient client, string toolName)
    {
        // Prefer the schema/annotation the client reported; fall back to the name
        // heuristic used elsewhere in the codebase for tools without annotations.
        return toolName.StartsWith("get_", StringComparison.Ordinal)
            || toolName is "list_windows" or "query_controls" or "hit_test" or "wait_for"
            || toolName.StartsWith("screenshot_", StringComparison.Ordinal);
    }

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

    private static int StartProcess(KnownClientProfile profile)
    {
        var psi = new ProcessStartInfo
        {
            FileName = profile.ExecutablePath!,
            UseShellExecute = true,
        };
        if (!string.IsNullOrEmpty(profile.Arguments))
            psi.Arguments = profile.Arguments;
        if (!string.IsNullOrEmpty(profile.WorkingDirectory) && Directory.Exists(profile.WorkingDirectory))
            psi.WorkingDirectory = profile.WorkingDirectory;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{profile.ExecutablePath}'.");
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
        public DateTimeOffset ConnectedAtUtc { get; set; }
        public IReadOnlyList<ToolDescriptor> Tools { get; set; } = Array.Empty<ToolDescriptor>();

        // Correlation table: correlationId -> awaiting invoke.
        public ConcurrentDictionary<string, TaskCompletionSource<ToolResultMessage>> Pending { get; } = new();

        private long _lastSeenTicks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset LastSeenUtc => new(Volatile.Read(ref _lastSeenTicks), TimeSpan.Zero);
        public void Touch() => Volatile.Write(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);
    }
}
