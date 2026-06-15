using System.Collections.Concurrent;
using System.Text.Json;
using Keincheck.Protocol;

namespace Keincheck.Broker.Tests;

/// <summary>
/// A real-pipe broker harness for the Stage-2 IPC integration tests. It does what the
/// production Phase-B broker does on the wire — accept clients on
/// <see cref="PipeNames.ControlPipe"/>-style named pipes, track live + previously-seen
/// clients under a stable identifier, advertise the active client's tools, forward
/// <see cref="InvokeToolMessage"/>s and correlate the matching
/// <see cref="ToolResultMessage"/> by <see cref="MessageEnvelope.CorrelationId"/>, and
/// raise connected/updated/down events — using the <b>real</b> <see cref="PipeTransport"/>,
/// <see cref="PipeChannel"/> and Protocol DTOs. It deliberately mirrors the shape of
/// <c>Keincheck.Hub.IClientBroker</c>/<c>ClientInfo</c> without referencing the Hub
/// assembly, so the IPC contract is exercised end to end over a genuine
/// <see cref="System.IO.Pipes.NamedPipeServerStream"/> pair.
/// </summary>
internal sealed class PipeBrokerHarness : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly TimeSpan _invokeTimeout;
    private readonly object _gate = new();
    private readonly Dictionary<string, BrokerClientInfo> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolResultMessage>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private string? _active;
    private int _disposed;

    public PipeBrokerHarness(string pipeName, TimeSpan? invokeTimeout = null)
    {
        _pipeName = pipeName;
        _invokeTimeout = invokeTimeout ?? TimeSpan.FromSeconds(10);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public event EventHandler<BrokerClientInfo>? ClientConnected;
    public event EventHandler<BrokerClientInfo>? ClientUpdated;
    public event EventHandler<BrokerClientInfo>? ClientDown;

    /// <summary>The client whose tools the broker currently advertises (null = none).</summary>
    public string? ActiveClientId
    {
        get { lock (_gate) return _active; }
        set
        {
            BrokerClientInfo? info;
            lock (_gate)
            {
                _active = value;
                info = value is not null && _known.TryGetValue(value, out var c) ? c : null;
            }
            if (info is not null)
                ClientUpdated?.Invoke(this, info);
        }
    }

    /// <summary>Live pipe sessions only.</summary>
    public IReadOnlyList<BrokerClientInfo> ListClients()
    {
        lock (_gate) return _known.Values.Where(c => c.IsConnected).ToList();
    }

    /// <summary>Live + previously-seen clients (the registry used for launch/restart).</summary>
    public IReadOnlyList<BrokerClientInfo> ListKnownClients()
    {
        lock (_gate) return _known.Values.ToList();
    }

    public BrokerClientInfo? ClientStatus(string clientId)
    {
        lock (_gate) return _known.TryGetValue(clientId, out var c) ? c : null;
    }

    /// <summary>
    /// The hub-owned per-app read-only toggle. Setting it marks the client so the
    /// broker refuses mutating tool invocations, and raises <see cref="ClientUpdated"/>.
    /// </summary>
    public void SetReadOnly(string clientId, bool readOnly)
    {
        BrokerClientInfo? info = null;
        lock (_gate)
        {
            if (_known.TryGetValue(clientId, out var prior) && prior.ReadOnly != readOnly)
            {
                info = prior with { ReadOnly = readOnly };
                _known[clientId] = info;
            }
        }
        if (info is not null)
            ClientUpdated?.Invoke(this, info);
    }

    /// <summary>
    /// Forwards a tool call to the owning client over the pipe and awaits its
    /// <see cref="ToolResultMessage"/>, correlated by a fresh correlation id. Throws if
    /// the client is not connected, is read-only and the tool mutates, or times out.
    /// </summary>
    public async Task<ToolResultMessage> InvokeOnClientAsync(
        string clientId, string toolName, JsonElement? argumentsJson, CancellationToken ct = default)
    {
        ClientSession session;
        bool readOnly;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(clientId, out var s) || !_known.TryGetValue(clientId, out var info) || !info.IsConnected)
                throw new InvalidOperationException($"Client '{clientId}' is not connected.");
            session = s;
            readOnly = info.ReadOnly;
        }

        // Read-only gate: refuse a mutating tool before it ever hits the wire. The
        // production broker derives mutating-ness from the tool's annotations; the
        // harness uses the same read-only inspection/screenshot heuristic the client
        // applies so the rejection is symmetric.
        if (readOnly && IsMutatingTool(toolName))
            throw new InvalidOperationException($"Client '{clientId}' is read-only; '{toolName}' is refused.");

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = tcs;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(_invokeTimeout);

            await session.Channel.SendAsync(MessageKind.InvokeTool, new InvokeToolMessage
            {
                ClientId = clientId,
                ToolName = toolName,
                Arguments = argumentsJson,
            }, correlationId, linked.Token).ConfigureAwait(false);

            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = PipeTransport.CreateServerStream(_pipeName);
            PipeChannel channel;
            try
            {
                channel = await PipeTransport.AcceptAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeClientAsync(channel, ct), ct);
        }
    }

    private async Task ServeClientAsync(PipeChannel channel, CancellationToken ct)
    {
        string? clientId = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var envelope = await channel.ReceiveAsync(ct).ConfigureAwait(false);
                if (envelope is null)
                    break; // clean EOF — peer closed

                switch (envelope.Kind)
                {
                    case MessageKind.Register:
                        clientId = HandleRegister(channel, envelope);
                        break;
                    case MessageKind.ToolList:
                        HandleToolList(envelope);
                        break;
                    case MessageKind.ToolResult:
                        HandleToolResult(envelope);
                        break;
                    case MessageKind.Heartbeat:
                        HandleHeartbeat(envelope);
                        break;
                    case MessageKind.ClientDown:
                        clientId = HandleClientDown(envelope) ?? clientId;
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (ProtocolException) { /* malformed frame — treat as a drop */ }
        catch (IOException) { /* transport reset — treat as a drop */ }
        finally
        {
            if (clientId is not null)
                MarkDown(clientId, graceful: false, reason: "transport drop");
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string HandleRegister(PipeChannel channel, MessageEnvelope envelope)
    {
        var reg = envelope.Unwrap<RegisterMessage>()
                  ?? throw new ProtocolException("Register payload was null.");

        BrokerClientInfo info;
        bool isNew;
        lock (_gate)
        {
            // Stable-identifier reuse: a previously-seen id is reclaimed by its
            // reconnecting app (restart reserves + reuses the identifier). Carry the
            // recorded executable path / read-only flag forward across the reconnect.
            _known.TryGetValue(reg.ClientId, out var prior);
            isNew = prior is null;
            info = new BrokerClientInfo
            {
                ClientId = reg.ClientId,
                AppId = reg.ClientId,
                DisplayName = reg.DisplayName ?? prior?.DisplayName ?? reg.ClientId,
                ProcessId = reg.ProcessId,
                IsConnected = true,
                ReadOnly = prior?.ReadOnly ?? false,
                Tools = prior?.Tools ?? Array.Empty<ToolDescriptor>(),
                ExecutablePath = prior?.ExecutablePath,
                LastSeenUtc = DateTimeOffset.UtcNow,
            };
            _known[reg.ClientId] = info;
            _sessions[reg.ClientId] = new ClientSession(channel);
        }

        (isNew ? ClientConnected : ClientUpdated)?.Invoke(this, info);
        return reg.ClientId;
    }

    private void HandleToolList(MessageEnvelope envelope)
    {
        var list = envelope.Unwrap<ToolListMessage>();
        if (list is null) return;

        BrokerClientInfo? info = null;
        lock (_gate)
        {
            if (_known.TryGetValue(list.ClientId, out var prior))
            {
                info = prior with { Tools = list.Tools, LastSeenUtc = DateTimeOffset.UtcNow };
                _known[list.ClientId] = info;
            }
        }
        if (info is not null)
            ClientUpdated?.Invoke(this, info);
    }

    private void HandleToolResult(MessageEnvelope envelope)
    {
        var correlationId = envelope.CorrelationId;
        if (correlationId is null) return;

        var result = envelope.Unwrap<ToolResultMessage>();
        if (result is null) return;

        if (_pending.TryGetValue(correlationId, out var tcs))
            tcs.TrySetResult(result);
    }

    private void HandleHeartbeat(MessageEnvelope envelope)
    {
        var hb = envelope.Unwrap<HeartbeatMessage>();
        if (hb is null) return;

        lock (_gate)
        {
            if (_known.TryGetValue(hb.ClientId, out var prior))
                _known[hb.ClientId] = prior with { LastSeenUtc = DateTimeOffset.UtcNow };
        }
    }

    private string? HandleClientDown(MessageEnvelope envelope)
    {
        var down = envelope.Unwrap<ClientDownMessage>();
        if (down is null) return null;
        MarkDown(down.ClientId, down.Graceful, down.Reason);
        return down.ClientId;
    }

    private void MarkDown(string clientId, bool graceful, string? reason)
    {
        BrokerClientInfo? info;
        lock (_gate)
        {
            if (!_known.TryGetValue(clientId, out var prior) || !prior.IsConnected)
                return; // already down / unknown — single ClientDown per disconnect
            info = prior with { IsConnected = false, LastSeenUtc = DateTimeOffset.UtcNow };
            _known[clientId] = info; // retained as known (restart can reclaim the id)
            _sessions.Remove(clientId);
            if (_active == clientId)
                _active = null;
        }

        // Fail any in-flight invokes for this client so callers don't hang on a drop.
        foreach (var kv in _pending)
            kv.Value.TrySetException(new InvalidOperationException($"Client '{clientId}' went down ({reason})."));

        ClientDown?.Invoke(this, info);
    }

    /// <summary>
    /// Same read-only classification the client applies: <c>get_*</c>, the well-known
    /// read-only inspection tools, and <c>screenshot_*</c> are non-mutating; everything
    /// else mutates. Kept identical so the broker-side and client-side gates agree.
    /// </summary>
    internal static bool IsMutatingTool(string name) => !(
        name.StartsWith("get_", StringComparison.Ordinal)
        || name is "list_windows" or "query_controls" or "hit_test" or "wait_for"
        || name.StartsWith("screenshot_", StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();

        List<ClientSession> sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }
        foreach (var s in sessions)
            await s.Channel.DisposeAsync().ConfigureAwait(false);

        // Unblock the accept loop, which is parked in WaitForConnectionAsync, by poking
        // the pipe with a throwaway client connect. Cancellation handles the rest.
        try
        {
            await using var poke = await PipeTransport.ConnectAsync(_pipeName, TimeSpan.FromMilliseconds(250))
                .ConfigureAwait(false);
        }
        catch { /* the loop may already be torn down */ }

        try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private sealed class ClientSession(PipeChannel channel)
    {
        public PipeChannel Channel { get; } = channel;
    }
}

/// <summary>
/// Test-side mirror of <c>Keincheck.Hub.ClientInfo</c> (the Hub assembly isn't
/// referenced here — these IPC tests live on the Protocol contract). Field-for-field
/// the same so the harness exercises the identical registry shape.
/// </summary>
internal sealed record BrokerClientInfo
{
    public required string ClientId { get; init; }
    public string? AppId { get; init; }
    public string? DisplayName { get; init; }
    public int ProcessId { get; init; }
    public bool IsConnected { get; init; }
    public bool ReadOnly { get; init; }
    public IReadOnlyList<ToolDescriptor> Tools { get; init; } = Array.Empty<ToolDescriptor>();
    public string? ExecutablePath { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
}
