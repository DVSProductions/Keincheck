using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace Keincheck.Hub;

/// <summary>
/// A single row in the tray window's client list. Wraps a <see cref="ClientInfo"/>
/// snapshot plus the live/active state the UI cares about.
/// </summary>
public sealed class ClientRow : INotifyPropertyChanged
{
    private ClientInfo _info;
    private bool _isActive;

    public ClientRow(ClientInfo info, bool isActive)
    {
        _info = info;
        _isActive = isActive;
    }

    public ClientInfo Info
    {
        get => _info;
        set
        {
            _info = value;
            RaiseAll();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; Raise(nameof(IsActive)); Raise(nameof(StatusLine)); } }
    }

    /// <summary>
    /// The agent currently allowed to drive this instance, or null when it is free. Shown so
    /// the operator can see which of several agents owns which copy of an app.
    /// </summary>
    public string? ClaimedBy
    {
        get => _claimedBy;
        set { if (_claimedBy != value) { _claimedBy = value; Raise(nameof(ClaimedBy)); Raise(nameof(IsClaimed)); Raise(nameof(StatusLine)); } }
    }

    /// <summary>True when some agent holds this instance (drives the release affordance).</summary>
    public bool IsClaimed => _claimedBy is { Length: > 0 };

    private string? _claimedBy;

    public string ClientId => _info.ClientId;

    /// <summary>
    /// The row's title. A remote client is suffixed with the machine it runs on, so it is
    /// never mistaken for the local instance of the same app sitting next to it in the list.
    /// </summary>
    public string Display
    {
        get
        {
            var name = string.IsNullOrEmpty(_info.DisplayName) ? _info.ClientId : _info.DisplayName!;
            return _info.Host is { Length: > 0 } host ? $"{name}  @{host}" : name;
        }
    }

    public bool IsConnected => _info.IsConnected;
    public bool ReadOnly => _info.ReadOnly;
    public int ToolCount => _info.Tools.Count;

    /// <summary>True when this client is on another machine (drives the row's remote badge).</summary>
    public bool IsRemote => _info.IsRemote;

    /// <summary>False for remote clients, which the hub cannot launch or restart.</summary>
    public bool CanLaunch => _info.CanLaunch;

    public string StatusLine
    {
        get
        {
            var state = _info.IsConnected ? "connected" : "offline";
            var ro = _info.ReadOnly ? " · read-only" : string.Empty;
            var active = _isActive ? " · ACTIVE" : string.Empty;
            var pid = _info.ProcessId > 0 && !_info.IsRemote ? $" · pid {_info.ProcessId}" : string.Empty;
            // Say "remote" outright. The operator needs to know at a glance that a line in
            // this list represents a machine that is not in front of them.
            var where = _info.IsRemote ? " · REMOTE" : string.Empty;
            var driver = _claimedBy is { Length: > 0 } who ? $" · driven by {who}" : string.Empty;
            return $"{state}{where}{pid} · {_info.Tools.Count} tools{ro}{active}{driver}";
        }
    }

    private void RaiseAll()
    {
        foreach (var p in new[]
        {
            nameof(Display), nameof(IsConnected), nameof(ReadOnly), nameof(ToolCount),
            nameof(StatusLine), nameof(IsRemote), nameof(CanLaunch),
            nameof(ClaimedBy), nameof(IsClaimed),
        })
            Raise(p);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The tray window's view-model. Mirrors the broker's registry + audit log into
/// observable collections, marshaling every broker event onto the UI thread. Exposes
/// the operator actions (launch / restart / read-only toggle / select-active) the
/// window binds buttons to.
/// </summary>
public sealed class HubViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClientBroker _broker;
    private readonly HubMcpServer? _mcp;
    private string? _drivingText;

    public HubViewModel(PipeClientBroker broker, HubMcpServer? mcp = null)
    {
        _broker = broker;
        _mcp = mcp;

        foreach (var c in _broker.ListKnownClients())
            Clients.Add(new ClientRow(c, c.ClientId == _broker.DefaultClientId));
        foreach (var e in _broker.Audit.Snapshot())
            Audit.Add(e.Summary);

        _broker.ClientConnected += OnClientChanged;
        _broker.ClientUpdated += OnClientChanged;
        _broker.ClientDown += OnClientChanged;
        _broker.Audit.EntryAdded += OnAudit;

        if (_mcp is not null)
        {
            _mcp.SessionsChanged += OnSessionsChanged;
            _mcp.Claims.Changed += OnSessionsChanged;
        }

        UpdateDriving();
        UpdateClaims();
    }

    /// <summary>The live client rows shown in the window.</summary>
    public ObservableCollection<ClientRow> Clients { get; } = new();

    /// <summary>Recent AI tool-call lines.</summary>
    public ObservableCollection<string> Audit { get; } = new();

    /// <summary>
    /// The "AI is driving X" banner. With several agents connected it lists one line each, so
    /// the operator can see who is driving what rather than a single misleading answer.
    /// </summary>
    public string DrivingText
    {
        get => _drivingText ?? "No AI agent connected.";
        private set { _drivingText = value; Raise(nameof(DrivingText)); }
    }

    /// <summary>
    /// Points every connected agent at <paramref name="clientId"/>. A deliberate operator
    /// override: the human at the machine outranks what the agents chose for themselves.
    /// </summary>
    public void SelectActive(string clientId)
    {
        _broker.DefaultClientId = clientId;
        _mcp?.SelectForAllSessions(clientId);
        OnUi(() =>
        {
            foreach (var row in Clients)
                row.IsActive = row.ClientId == clientId;
            UpdateDriving();
        });
    }

    /// <summary>
    /// Frees an app an agent is holding, so somebody else can drive it. The unblock hatch for
    /// an agent that died without releasing and is not yet past the idle timeout.
    /// </summary>
    public void ReleaseClaim(string clientId)
    {
        if (_mcp is null)
            return;
        _mcp.Claims.Release(clientId, sessionId: null, force: true);
        UpdateClaims();
    }

    /// <summary>Launches a known/offline app from its recorded profile.</summary>
    public async Task LaunchAsync(string clientId)
    {
        try { await _broker.LaunchClientAsync(clientId).ConfigureAwait(false); }
        catch (Exception ex) { Note($"launch '{clientId}' failed: {ex.Message}"); }
    }

    /// <summary>Restarts a client (kills the tracked pid, relaunches, keeps the id).</summary>
    public async Task RestartAsync(string clientId)
    {
        try { await _broker.RestartClientAsync(clientId).ConfigureAwait(false); }
        catch (Exception ex) { Note($"restart '{clientId}' failed: {ex.Message}"); }
    }

    /// <summary>Toggles a client's read-only flag.</summary>
    public void SetReadOnly(string clientId, bool readOnly) => _broker.SetReadOnly(clientId, readOnly);

    // ---- broker -> UI ----------------------------------------------------

    private void OnClientChanged(object? sender, ClientInfo info) => OnUi(() =>
    {
        var existing = Clients.FirstOrDefault(c => c.ClientId == info.ClientId);
        if (existing is null)
            Clients.Add(new ClientRow(info, info.ClientId == _broker.DefaultClientId));
        else
            existing.Info = info;

        foreach (var row in Clients)
            row.IsActive = row.ClientId == _broker.DefaultClientId;
        UpdateDriving();
        UpdateClaims();
    });

    private void OnAudit(object? sender, AuditEntry entry) => OnUi(() =>
    {
        Audit.Add(entry.Summary);
        while (Audit.Count > 200)
            Audit.RemoveAt(0);
    });

    private void OnSessionsChanged() => OnUi(() =>
    {
        UpdateDriving();
        UpdateClaims();
    });

    /// <summary>Mirrors the claim registry onto the rows so each shows who is driving it.</summary>
    private void UpdateClaims()
    {
        if (_mcp is null)
            return;

        var byClient = _mcp.Claims.Snapshot().ToDictionary(c => c.ClientId, c => c.SessionLabel, StringComparer.Ordinal);
        foreach (var row in Clients)
            row.ClaimedBy = byClient.GetValueOrDefault(row.ClientId);
    }

    private void UpdateDriving()
    {
        // No MCP server wired in (the designer/previewer path): fall back to the hub-wide
        // default, which is all that host knows about.
        var sessions = _mcp?.SessionSnapshots();
        if (sessions is null)
        {
            var fallbackId = _broker.DefaultClientId;
            DrivingText = fallbackId is null
                ? "No active client — the AI sees only the hub meta-tools."
                : $"AI target: {Clients.FirstOrDefault(c => c.ClientId == fallbackId)?.Display ?? fallbackId}";
            return;
        }

        if (sessions.Count == 0)
        {
            DrivingText = "No AI agent connected.";
            return;
        }

        var lines = sessions
            .OrderBy(s => s.ConnectedUtc)
            .Select(s =>
            {
                var label = s.Label.Length > 0 ? s.Label : "connecting…";
                if (s.ActiveClientId is not { } id)
                    return $"{label} → (no client selected)";
                var name = Clients.FirstOrDefault(c => c.ClientId == id)?.Display ?? id;
                return $"{label} → {name}";
            });

        DrivingText = string.Join(Environment.NewLine, lines);
    }

    private void Note(string line) => OnUi(() =>
    {
        Audit.Add($"{DateTimeOffset.Now:HH:mm:ss}  ! {line}");
    });

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        _broker.ClientConnected -= OnClientChanged;
        _broker.ClientUpdated -= OnClientChanged;
        _broker.ClientDown -= OnClientChanged;
        _broker.Audit.EntryAdded -= OnAudit;

        if (_mcp is not null)
        {
            _mcp.SessionsChanged -= OnSessionsChanged;
            _mcp.Claims.Changed -= OnSessionsChanged;
        }
    }
}
