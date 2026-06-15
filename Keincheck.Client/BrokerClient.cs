using System.Diagnostics;
using System.Text.Json;
using Keincheck.Core;
using Keincheck.Protocol;
using ModelContextProtocol.Protocol;

namespace Keincheck.Client;

/// <summary>
/// The in-app broker client. Connects to the hub over the named pipe, registers the
/// app, reports its Core tool catalog, then services <see cref="InvokeToolMessage"/>
/// requests on the UI thread and replies with <see cref="ToolResultMessage"/>. Sends
/// periodic heartbeats, auto-reconnects on drop, and says goodbye on shutdown.
/// </summary>
public sealed class BrokerClient : IAsyncDisposable
{
    private readonly IUiAdapter _adapter;
    private readonly IUiDispatcher _dispatcher;
    private readonly McpClientOptions _options;
    private readonly Keincheck.Core.McpServerOptions _coreOptions;
    private readonly string _clientId;
    private readonly string _displayName;
    private readonly CancellationTokenSource _cts = new();

    private ClientToolHost? _toolHost;
    private Task? _runLoop;
    private long _heartbeatSeq;

    private BrokerClient(IUiAdapter adapter, IUiDispatcher dispatcher, McpClientOptions options)
    {
        _adapter = adapter;
        _dispatcher = dispatcher;
        _options = options;
        _coreOptions = options.CoreOptions ?? new Keincheck.Core.McpServerOptions();
        _clientId = string.IsNullOrWhiteSpace(options.AppId)
            ? (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "app")
            : options.AppId!;
        _displayName = options.DisplayName ?? _clientId;
    }

    /// <summary>The stable id this client registered under.</summary>
    public string ClientId => _clientId;

    /// <summary>
    /// Builds the tool host (over the injected framework adapter/dispatcher) and starts
    /// the connect/serve loop on a background task. Returns immediately; the client
    /// connects and reconnects on its own.
    /// </summary>
    public static BrokerClient Start(IUiAdapter adapter, IUiDispatcher dispatcher, McpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);

        var client = new BrokerClient(adapter, dispatcher, options);
        client._toolHost = ClientToolHost.Build(adapter, dispatcher, client._coreOptions);
        client._runLoop = Task.Run(() => client.RunAsync(client._cts.Token));
        return client;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var channel = await PipeTransport.ConnectAsync(
                    _options.PipeName, _options.ConnectTimeout, ct).ConfigureAwait(false);

                backoff = TimeSpan.FromMilliseconds(250); // reset after a good connect
                await ServeAsync(channel, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Keincheck.Client] session ended: {ex.Message}");
            }

            if (!_options.AutoReconnect || ct.IsCancellationRequested)
                break;

            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 5000));
        }
    }

    private async Task ServeAsync(PipeChannel channel, CancellationToken ct)
    {
        // 1. Register.
        await channel.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = _clientId,
            DisplayName = _displayName,
            ProcessId = Environment.ProcessId,
            ProtocolVersion = ProtocolVersion.Current,
            OwnsWindows = await OwnsWindowsAsync(ct).ConfigureAwait(false),
        }, cancellationToken: ct).ConfigureAwait(false);

        // 2. Report tool catalog. ownsWindows is recomputed here (not just at register)
        //    because a process whose windows open after startup only becomes the
        //    UI-owner once they exist.
        await channel.SendAsync(MessageKind.ToolList, new ToolListMessage
        {
            ClientId = _clientId,
            Tools = _toolHost!.Describe(),
            OwnsWindows = await OwnsWindowsAsync(ct).ConfigureAwait(false),
        }, cancellationToken: ct).ConfigureAwait(false);

        // 3. Start heartbeat pump alongside the receive loop.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(channel, linked.Token);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var envelope = await channel.ReceiveAsync(ct).ConfigureAwait(false);
                if (envelope is null)
                    break; // hub closed the connection

                if (envelope.Kind == MessageKind.InvokeTool)
                    _ = HandleInvokeAsync(channel, envelope, ct);
            }
        }
        finally
        {
            linked.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* ignore */ }

            // Send a graceful goodbye only when we're shutting down on purpose; on a
            // hub-side drop the pipe is already dead and the hub infers the down state
            // from the closed connection. The send is still best-effort and bounded so
            // a wedged pipe can't block app exit.
            if (ct.IsCancellationRequested)
            {
                try
                {
                    using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await channel.SendAsync(MessageKind.ClientDown, new ClientDownMessage
                    {
                        ClientId = _clientId,
                        Graceful = true,
                        Reason = "app shutting down",
                    }, cancellationToken: bye.Token).ConfigureAwait(false);
                }
                catch { /* channel may already be dead — ignore */ }
            }
        }
    }

    /// <summary>
    /// Whether this app currently owns any top-level window — reported so the hub can
    /// flag the UI-owning client among several registrants of the same app. Computed on
    /// the UI thread (<see cref="IUiAdapter.EnumerateRoots"/>/<see cref="IUiAdapter.IsControl"/>
    /// are UI-thread only) and is strictly best-effort: any failure reports false rather
    /// than blocking registration.
    /// </summary>
    private async Task<bool> OwnsWindowsAsync(CancellationToken ct)
    {
        try
        {
            return await _dispatcher.Run(() =>
            {
                foreach (var root in _adapter.EnumerateRoots())
                {
                    if (_adapter.IsControl(root))
                        return true;
                }
                return false;
            }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Keincheck.Client] ownsWindows probe failed: {ex.Message}");
            return false;
        }
    }

    private async Task HeartbeatAsync(PipeChannel channel, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false);
                await channel.SendAsync(MessageKind.Heartbeat, new HeartbeatMessage
                {
                    ClientId = _clientId,
                    Sequence = Interlocked.Increment(ref _heartbeatSeq),
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }, cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception ex) { Debug.WriteLine($"[Keincheck.Client] heartbeat stopped: {ex.Message}"); }
    }

    private async Task HandleInvokeAsync(PipeChannel channel, MessageEnvelope envelope, CancellationToken ct)
    {
        var invoke = envelope.Unwrap<InvokeToolMessage>();
        var correlationId = envelope.CorrelationId;
        if (invoke is null)
            return;

        var result = new ToolResultMessage { ClientId = _clientId, ToolName = invoke.ToolName };
        try
        {
            // Local read-only enforcement: a read-only client refuses any tool the host
            // cannot prove is side-effect-free (mutating Core tools and unknown names).
            if (_options.ReadOnly && !_toolHost!.IsToolReadOnly(invoke.ToolName))
            {
                result.IsError = true;
                result.Error = $"Client '{_clientId}' is read-only; the mutating tool '{invoke.ToolName}' is refused.";
            }
            else
            {
                var callResult = await _toolHost!.InvokeAsync(invoke.ToolName, invoke.Arguments, ct).ConfigureAwait(false);
                result.IsError = callResult.IsError ?? false;
                result.Content = JsonSerializer.SerializeToElement(callResult.Content, ProtocolJson.Options);
            }
        }
        catch (Exception ex)
        {
            result.IsError = true;
            result.Error = ex.Message;
        }

        try
        {
            await channel.SendAsync(MessageKind.ToolResult, result, correlationId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Keincheck.Client] failed to send result for {invoke.ToolName}: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_runLoop is not null)
        {
            try { await _runLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _toolHost?.Dispose();
        _cts.Dispose();
    }
}
