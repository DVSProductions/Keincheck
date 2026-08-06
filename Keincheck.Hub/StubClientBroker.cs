using System.Text.Json;
using Keincheck.Protocol;

namespace Keincheck.Hub;

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
    public string? DefaultClientId
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

    /// <summary>Test/seam hook: how the stub satisfies launch and restart.</summary>
    public Func<string, LaunchOptions?, LaunchResult>? LaunchHandler { get; set; }

    /// <inheritdoc/>
    public Task<LaunchResult> LaunchClientAsync(
        string clientId, LaunchOptions? launch = null, CancellationToken cancellationToken = default)
        => LaunchHandler is { } handler
            ? Task.FromResult(handler(clientId, launch))
            : throw new NotSupportedException("StubClientBroker cannot launch apps; set LaunchHandler.");

    /// <inheritdoc/>
    public Task<LaunchResult> RestartClientAsync(
        string clientId, LaunchOptions? launch = null, CancellationToken cancellationToken = default)
        => LaunchHandler is { } handler
            ? Task.FromResult(handler(clientId, launch))
            : throw new NotSupportedException("StubClientBroker cannot restart apps; set LaunchHandler.");

    /// <summary>Simulates a hub-launched client finishing registration.</summary>
    public void RaiseLaunchRegistered(ClientInfo info)
    {
        Upsert(info);
        LaunchRegistered?.Invoke(this, info);
    }

    /// <inheritdoc/>
    public Task<ClientInfo?> WaitForClientAsync(
        string? appIdOrClientId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // The stub does not run a pipe loop, so there is nothing to wait for: report the
        // first already-connected match (or null). Tests drive connections via Upsert.
        lock (_gate)
        {
            foreach (var c in _known.Values)
            {
                if (!c.IsConnected)
                    continue;
                if (string.IsNullOrWhiteSpace(appIdOrClientId)
                    || string.Equals(c.ClientId, appIdOrClientId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.AppId, appIdOrClientId, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<ClientInfo?>(c);
                }
            }
        }
        return Task.FromResult<ClientInfo?>(null);
    }

    /// <inheritdoc/>
    public Task<ClientInfo?> WaitForClientAsync(
        ClientWaitFilter filter, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var c in _known.Values)
            {
                if (!c.IsConnected)
                    continue;
                if (filter.LaunchId is { Length: > 0 } launchId)
                {
                    if (string.Equals(c.LaunchId, launchId, StringComparison.Ordinal))
                        return Task.FromResult<ClientInfo?>(c);
                    continue;
                }
                if (filter.ProcessId is { } pid && c.ProcessId != pid)
                    continue;
                if (string.IsNullOrWhiteSpace(filter.IdOrAppId)
                    || string.Equals(c.ClientId, filter.IdOrAppId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.AppId, filter.IdOrAppId, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<ClientInfo?>(c);
                }
            }
        }
        return Task.FromResult<ClientInfo?>(null);
    }

    /// <inheritdoc/>
    public Task<ToolResultMessage> InvokeOnClientAsync(
        string clientId, string toolName, JsonElement? argumentsJson,
        CancellationToken cancellationToken = default, string? agent = null)
    {
        if (InvokeHandler is null)
            throw new InvalidOperationException("StubClientBroker.InvokeHandler is not set.");
        LastAgent = agent;
        return InvokeHandler(clientId, toolName, argumentsJson, cancellationToken);
    }

    /// <summary>The agent label passed to the most recent invoke (test seam).</summary>
    public string? LastAgent { get; private set; }

    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientConnected;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientUpdated;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? ClientDown;
    /// <inheritdoc/>
    public event EventHandler<ClientInfo>? LaunchRegistered;
}
