using System.Text.Json;
using AvaloniaMcp.Protocol;

namespace AvaloniaMcp.Hub;

/// <summary>
/// A minimal in-memory <see cref="IClientBroker"/> so the Hub project compiles and
/// the MCP server can be exercised before the real pipe-server broker lands. Phase-B
/// replaces this with the live broker (pipe accept loop, registry, launcher). The
/// stub lets you register fake clients and wire an invoke callback for tests.
/// </summary>
public sealed class StubClientBroker : IClientBroker
{
    private readonly Dictionary<string, ClientInfo> _known = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string? _active;

    /// <summary>
    /// Test/seam hook: how the stub satisfies <see cref="InvokeOnClientAsync"/>.
    /// Phase-B's real broker forwards over the pipe instead.
    /// </summary>
    public Func<string, string, JsonElement?, CancellationToken, Task<ToolResultMessage>>? InvokeHandler { get; set; }

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
                info = value is not null && _known.TryGetValue(value, out var c) ? c : null;
            }
            if (info is not null)
                ClientUpdated?.Invoke(this, info);
        }
    }

    /// <summary>Registers (or updates) a client snapshot and raises the right event.</summary>
    public void Upsert(ClientInfo info)
    {
        bool isNew;
        lock (_gate)
        {
            isNew = !_known.ContainsKey(info.ClientId);
            _known[info.ClientId] = info;
        }
        (isNew ? ClientConnected : ClientUpdated)?.Invoke(this, info);
    }

    /// <summary>Marks a client disconnected and raises <see cref="ClientDown"/>.</summary>
    public void MarkDown(string clientId)
    {
        ClientInfo? info;
        lock (_gate)
        {
            if (!_known.TryGetValue(clientId, out var c))
                return;
            info = c with { IsConnected = false };
            _known[clientId] = info;
        }
        ClientDown?.Invoke(this, info);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ClientInfo> ListClients()
    {
        lock (_gate) return _known.Values.Where(c => c.IsConnected).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<ClientInfo> ListKnownClients()
    {
        lock (_gate) return _known.Values.ToList();
    }

    /// <inheritdoc/>
    public ClientInfo? ClientStatus(string clientId)
    {
        lock (_gate) return _known.TryGetValue(clientId, out var c) ? c : null;
    }

    /// <inheritdoc/>
    public Task<int> LaunchClientAsync(string clientId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("StubClientBroker cannot launch apps; Phase-B broker implements this.");

    /// <inheritdoc/>
    public Task<int> RestartClientAsync(string clientId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("StubClientBroker cannot restart apps; Phase-B broker implements this.");

    /// <inheritdoc/>
    public Task<ToolResultMessage> InvokeOnClientAsync(
        string clientId, string toolName, JsonElement? argumentsJson, CancellationToken cancellationToken = default)
    {
        if (InvokeHandler is null)
            throw new InvalidOperationException("StubClientBroker.InvokeHandler is not set.");
        return InvokeHandler(clientId, toolName, argumentsJson, cancellationToken);
    }

    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientConnected;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientUpdated;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientDown;
}
