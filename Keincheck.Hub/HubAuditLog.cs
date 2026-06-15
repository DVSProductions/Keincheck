using System.Collections.ObjectModel;

namespace Keincheck.Hub;

/// <summary>The outcome category of an audited tool invocation.</summary>
public enum AuditOutcome
{
    /// <summary>The call is in flight (request forwarded, awaiting the client's result).</summary>
    Started,

    /// <summary>The client returned a successful result.</summary>
    Ok,

    /// <summary>The client returned an error, or the call failed/timed out/was refused.</summary>
    Error,
}

/// <summary>
/// One entry in the hub's audit trail: an AI-initiated tool call routed to a client.
/// Surfaced in the tray window so the operator can see exactly what the AI is doing.
/// </summary>
public sealed record AuditEntry
{
    /// <summary>When the call was forwarded.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The hub-assigned id of the client the call targeted.</summary>
    public required string ClientId { get; init; }

    /// <summary>The tool that was invoked.</summary>
    public required string ToolName { get; init; }

    /// <summary>The call outcome.</summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>An error message when <see cref="Outcome"/> is <see cref="AuditOutcome.Error"/>.</summary>
    public string? Error { get; init; }

    /// <summary>A short, human-readable one-liner for the tray log.</summary>
    public string Summary
    {
        get
        {
            var t = TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
            return Outcome switch
            {
                AuditOutcome.Started => $"{t}  → {ClientId}  {ToolName}",
                AuditOutcome.Ok => $"{t}  ✓ {ClientId}  {ToolName}",
                AuditOutcome.Error => $"{t}  ✗ {ClientId}  {ToolName}  — {Error}",
                _ => $"{t}  {ClientId}  {ToolName}",
            };
        }
    }
}

/// <summary>
/// A bounded, observable ring buffer of <see cref="AuditEntry"/> records. The broker
/// appends; the tray UI binds to <see cref="Entries"/>. Raises
/// <see cref="EntryAdded"/> on each append so a view-model can react without polling.
/// </summary>
/// <remarks>
/// The internal collection is mutated under a lock and the public
/// <see cref="ObservableCollection{T}"/> is updated through <see cref="EntryAdded"/>
/// subscribers, which are expected to marshal onto the UI thread themselves. The
/// broker never touches the UI directly.
/// </remarks>
public sealed class HubAuditLog
{
    private readonly object _gate = new();
    private readonly LinkedList<AuditEntry> _entries = new();
    private readonly int _capacity;

    /// <summary>Creates a log keeping at most <paramref name="capacity"/> recent entries.</summary>
    public HubAuditLog(int capacity = 500)
    {
        _capacity = Math.Max(16, capacity);
    }

    /// <summary>Raised after an entry is appended (on the calling/broker thread).</summary>
    public event EventHandler<AuditEntry>? EntryAdded;

    /// <summary>A snapshot of the buffered entries, newest last.</summary>
    public IReadOnlyList<AuditEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToList();
    }

    /// <summary>Appends an entry, evicting the oldest when over capacity.</summary>
    public void Add(AuditEntry entry)
    {
        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > _capacity)
                _entries.RemoveFirst();
        }
        EntryAdded?.Invoke(this, entry);
    }
}
