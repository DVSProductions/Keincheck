using System.Diagnostics;
using System.Reflection;
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
        // Whatever transport was configured is the ONLY one tried. A remote client that
        // quietly fell back to the local pipe would attach the operator to the wrong machine
        // while the tool surface looked completely normal.
        var connector = _options.Connector
            ?? new PipeChannelConnector(_options.PipeName, _options.ConnectTimeout);
        var context = new ChannelConnectContext
        {
            AppId = _clientId,
            ClientVersion = ClientAssemblyVersion,
            HeartbeatInterval = _options.HeartbeatInterval,
        };

        var backoff = TimeSpan.FromMilliseconds(250);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var session = await connector.ConnectAsync(context, ct).ConfigureAwait(false);
                await using var channel = session.Channel;

                backoff = TimeSpan.FromMilliseconds(250); // reset after a good connect
                await ServeAsync(channel, session.ReadTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Report($"session to {connector.Describe()} ended: {ex.Message}");

                // Some refusals will never succeed however long we wait — a revoked or expired
                // credential, an unsupported protocol version. Retrying those is not
                // resilience, it is a hot loop: a full TCP + mutual-TLS handshake every few
                // seconds, forever, each one logged hub-side as an authentication failure.
                // Stop, and say why, rather than hammering a door that is never opening.
                if (IsPermanentRefusal(ex, out var reason))
                {
                    Report($"giving up: {reason}");
                    break;
                }
            }

            if (!_options.AutoReconnect || ct.IsCancellationRequested)
                break;

            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 5000));
        }
    }

    private async Task ServeAsync(PipeChannel channel, TimeSpan? readTimeout, CancellationToken ct)
    {
        // 1. Register.
        await channel.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = _clientId,
            DisplayName = _displayName,
            ProcessId = Environment.ProcessId,
            ProtocolVersion = ProtocolVersion.Current,
            OwnsWindows = await OwnsWindowsAsync(ct).ConfigureAwait(false),
            ClientVersion = ClientAssemblyVersion,
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
                var envelope = await ReceiveAsync(channel, readTimeout, ct).ConfigureAwait(false);
                if (envelope is null)
                    break; // hub closed the connection

                if (envelope.Kind == MessageKind.InvokeTool)
                    _ = HandleInvokeAsync(channel, envelope, ct);

                // Heartbeat / Welcome / anything else: receiving it at all is the liveness
                // signal, which is the whole point of the read deadline below.
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

    /// <summary>Surfaces a diagnostic to the app's log hook, and always to Debug.</summary>
    private void Report(string message)
    {
        Debug.WriteLine($"[Keincheck.Client] {message}");
        try { _options.Log?.Invoke(message); } catch { /* a logging hook must never break the client */ }
    }

    /// <summary>
    /// Whether a failure means reconnecting can never succeed until something changes
    /// out-of-band, in which case the client stops instead of looping.
    /// </summary>
    private static bool IsPermanentRefusal(Exception ex, out string reason)
    {
        // Unwrap: the transport may have wrapped the refusal on its way out.
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is ChannelConnectRefusedException { IsPermanent: true } refused)
            {
                reason = refused.Message;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads one message, optionally bounded by an idle deadline.
    /// </summary>
    /// <remarks>
    /// On a pipe <paramref name="readTimeout"/> is null and this is a plain receive: a dead
    /// hub closes the pipe and we see EOF at once. On a network transport there is no such
    /// signal — a black-holed TCP connection is indistinguishable from a quiet one, so
    /// without a deadline a client whose link died mid-session would sit on a socket that is
    /// never going to answer, invisible to both ends. Timing out here drops into the
    /// reconnect loop, which is exactly the right behaviour for a roaming target.
    /// </remarks>
    private static async Task<MessageEnvelope?> ReceiveAsync(
        PipeChannel channel, TimeSpan? readTimeout, CancellationToken ct)
    {
        if (readTimeout is not { } timeout)
            return await channel.ReceiveAsync(ct).ConfigureAwait(false);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(timeout);
        try
        {
            return await channel.ReceiveAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No message from the hub within {timeout.TotalSeconds:0.#}s; treating the link as dead.");
        }
    }

    /// <summary>
    /// The informational version of the Keincheck.Client assembly this app links, reported on
    /// register so the hub can show which client build each app is compiled against. Prefers
    /// the <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/> (e.g. "0.5.0")
    /// and falls back to the plain assembly <see cref="System.Reflection.AssemblyName.Version"/>.
    /// Strictly best-effort: any failure reports null rather than blocking registration.
    /// </summary>
    private static readonly string? ClientAssemblyVersion = ResolveClientAssemblyVersion();

    private static string? ResolveClientAssemblyVersion()
    {
        try
        {
            var asm = typeof(BrokerClient).Assembly;
            var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            return string.IsNullOrWhiteSpace(info) ? asm.GetName().Version?.ToString() : info;
        }
        catch
        {
            return null;
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
