using System.Text.Json;
using Keincheck.Protocol;

namespace Keincheck.Broker.Tests;

/// <summary>
/// A thin in-process stand-in for <c>Keincheck.Client.BrokerClient</c> that speaks
/// the real wire protocol over a genuine <see cref="PipeChannel"/>: it connects to the
/// broker pipe, sends <see cref="RegisterMessage"/> + <see cref="ToolListMessage"/>,
/// then services <see cref="InvokeToolMessage"/>s by running a test-supplied handler and
/// replying with a correlated <see cref="ToolResultMessage"/>. Disposing it can either
/// send a graceful <see cref="ClientDownMessage"/> or just drop the transport, so the
/// tests can exercise both disconnect paths.
/// </summary>
internal sealed class FakeBrokerClient : IAsyncDisposable
{
    private readonly PipeChannel _channel;
    private readonly string _clientId;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;
    private readonly Func<InvokeToolMessage, ToolResultMessage> _onInvoke;
    private int _disposed;

    private FakeBrokerClient(
        PipeChannel channel, string clientId, Func<InvokeToolMessage, ToolResultMessage> onInvoke)
    {
        _channel = channel;
        _clientId = clientId;
        _onInvoke = onInvoke;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public string ClientId => _clientId;

    /// <summary>
    /// Connects to <paramref name="pipeName"/>, registers as <paramref name="clientId"/>,
    /// and reports <paramref name="tools"/>. <paramref name="onInvoke"/> (if null) echoes
    /// a single text content block back for every call.
    /// </summary>
    public static async Task<FakeBrokerClient> ConnectAsync(
        string pipeName,
        string clientId,
        IReadOnlyList<ToolDescriptor> tools,
        Func<InvokeToolMessage, ToolResultMessage>? onInvoke = null,
        string? displayName = null,
        int processId = 4321,
        CancellationToken ct = default)
    {
        var channel = await PipeTransport.ConnectAsync(pipeName, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        onInvoke ??= invoke => new ToolResultMessage
        {
            ClientId = clientId,
            ToolName = invoke.ToolName,
            IsError = false,
            Content = JsonSerializer.SerializeToElement(
                new object[] { new { type = "text", text = $"ran {invoke.ToolName} on {clientId}" } },
                ProtocolJson.Options),
        };

        var client = new FakeBrokerClient(channel, clientId, onInvoke);

        await channel.SendAsync(MessageKind.Register, new RegisterMessage
        {
            ClientId = clientId,
            DisplayName = displayName ?? clientId,
            ProcessId = processId,
            ProtocolVersion = ProtocolVersion.Current,
        }, cancellationToken: ct).ConfigureAwait(false);

        await channel.SendAsync(MessageKind.ToolList, new ToolListMessage
        {
            ClientId = clientId,
            Tools = tools,
        }, cancellationToken: ct).ConfigureAwait(false);

        return client;
    }

    /// <summary>Sends a single heartbeat (tests can drive liveness explicitly).</summary>
    public Task SendHeartbeatAsync(long sequence, CancellationToken ct = default)
        => _channel.SendAsync(MessageKind.Heartbeat, new HeartbeatMessage
        {
            ClientId = _clientId,
            Sequence = sequence,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, cancellationToken: ct);

    /// <summary>Sends a graceful goodbye then tears the transport down.</summary>
    public async Task DisconnectGracefullyAsync()
    {
        try
        {
            await _channel.SendAsync(MessageKind.ClientDown, new ClientDownMessage
            {
                ClientId = _clientId,
                Graceful = true,
                Reason = "app shutting down",
            }).ConfigureAwait(false);
        }
        catch { /* channel may already be dead */ }

        await DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var envelope = await _channel.ReceiveAsync(ct).ConfigureAwait(false);
                if (envelope is null)
                    break; // broker closed

                if (envelope.Kind != MessageKind.InvokeTool)
                    continue;

                var invoke = envelope.Unwrap<InvokeToolMessage>();
                if (invoke is null)
                    continue;

                ToolResultMessage result;
                try
                {
                    result = _onInvoke(invoke);
                }
                catch (Exception ex)
                {
                    result = new ToolResultMessage
                    {
                        ClientId = _clientId,
                        ToolName = invoke.ToolName,
                        IsError = true,
                        Error = ex.Message,
                    };
                }

                await _channel.SendAsync(MessageKind.ToolResult, result, envelope.CorrelationId, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
        catch (ProtocolException) { /* transport torn down */ }
        catch (IOException) { /* transport torn down */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        await _channel.DisposeAsync().ConfigureAwait(false);
        try { await _receiveLoop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
