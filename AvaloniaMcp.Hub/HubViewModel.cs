using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace AvaloniaMcp.Hub;

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

    public string ClientId => _info.ClientId;
    public string Display => string.IsNullOrEmpty(_info.DisplayName) ? _info.ClientId : _info.DisplayName!;
    public bool IsConnected => _info.IsConnected;
    public bool ReadOnly => _info.ReadOnly;
    public int ToolCount => _info.Tools.Count;

    public string StatusLine
    {
        get
        {
            var state = _info.IsConnected ? "connected" : "offline";
            var ro = _info.ReadOnly ? " · read-only" : string.Empty;
            var active = _isActive ? " · ACTIVE" : string.Empty;
            var pid = _info.ProcessId > 0 ? $" · pid {_info.ProcessId}" : string.Empty;
            return $"{state}{pid} · {_info.Tools.Count} tools{ro}{active}";
        }
    }

    private void RaiseAll()
    {
        foreach (var p in new[] { nameof(Display), nameof(IsConnected), nameof(ReadOnly), nameof(ToolCount), nameof(StatusLine) })
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
    private string? _drivingText;

    public HubViewModel(PipeClientBroker broker)
    {
        _broker = broker;

        foreach (var c in _broker.ListKnownClients())
            Clients.Add(new ClientRow(c, c.ClientId == _broker.ActiveClientId));
        foreach (var e in _broker.Audit.Snapshot())
            Audit.Add(e.Summary);

        _broker.ClientConnected += OnClientChanged;
        _broker.ClientUpdated += OnClientChanged;
        _broker.ClientDown += OnClientChanged;
        _broker.Audit.EntryAdded += OnAudit;

        UpdateDriving();
    }

    /// <summary>The live client rows shown in the window.</summary>
    public ObservableCollection<ClientRow> Clients { get; } = new();

    /// <summary>Recent AI tool-call lines.</summary>
    public ObservableCollection<string> Audit { get; } = new();

    /// <summary>The "AI is driving X" banner text (empty when idle / no active client).</summary>
    public string DrivingText
    {
        get => _drivingText ?? "No active client — the AI sees only the hub meta-tools.";
        private set { _drivingText = value; Raise(nameof(DrivingText)); }
    }

    /// <summary>Makes <paramref name="clientId"/> the active (AI-driven) client.</summary>
    public void SelectActive(string clientId)
    {
        _broker.ActiveClientId = clientId;
        OnUi(() =>
        {
            foreach (var row in Clients)
                row.IsActive = row.ClientId == clientId;
            UpdateDriving();
        });
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
            Clients.Add(new ClientRow(info, info.ClientId == _broker.ActiveClientId));
        else
            existing.Info = info;

        foreach (var row in Clients)
            row.IsActive = row.ClientId == _broker.ActiveClientId;
        UpdateDriving();
    });

    private void OnAudit(object? sender, AuditEntry entry) => OnUi(() =>
    {
        Audit.Add(entry.Summary);
        while (Audit.Count > 200)
            Audit.RemoveAt(0);
    });

    private void UpdateDriving()
    {
        var activeId = _broker.ActiveClientId;
        if (activeId is null)
        {
            DrivingText = "No active client — the AI sees only the hub meta-tools.";
            return;
        }

        var row = Clients.FirstOrDefault(c => c.ClientId == activeId);
        var name = row?.Display ?? activeId;
        DrivingText = row is { IsConnected: true }
            ? $"AI is driving: {name}"
            : $"AI target: {name} (offline)";
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
    }
}
