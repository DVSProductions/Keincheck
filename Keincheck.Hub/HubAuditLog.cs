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

/// <summary>What kind of event an <see cref="AuditEntry"/> records.</summary>
/// <remarks>
/// Until remote existed the trail only had to answer "what did the AI just do to my app",
/// and tool invocations covered that. Once a machine you cannot see can attach, the trail also
/// has to answer "who connected, when, and what were they allowed to do" — so attachment,
/// credential issuance and revocation are recorded too.
/// </remarks>
public enum AuditKind
{
    /// <summary>An AI-initiated tool call routed to a client.</summary>
    Invoke = 0,

    /// <summary>A remote client authenticated and attached.</summary>
    Attach,

    /// <summary>A remote client's session ended.</summary>
    Detach,

    /// <summary>A peer failed to authenticate or was refused.</summary>
    AuthFailure,

    /// <summary>A client's read-only flag was changed.</summary>
    Escalate,

    /// <summary>A remote credential was issued.</summary>
    Enroll,

    /// <summary>A remote credential was revoked.</summary>
    Revoke,

    /// <summary>Remote access was enabled or disabled.</summary>
    RemoteToggled,

    /// <summary>The hub started an app process on an agent's behalf.</summary>
    Launch,

    /// <summary>An agent took the exclusive right to drive an app instance.</summary>
    ClaimAcquired,

    /// <summary>An agent gave up (or lost) the right to drive an app instance.</summary>
    ClaimReleased,

    /// <summary>A mutating call was refused because another agent is driving that instance.</summary>
    ClaimDenied,
}

/// <summary>
/// One entry in the hub's audit trail. Surfaced in the tray window so the operator can see
/// exactly what is happening, and — once remote access is enabled — written to disk, because
/// a 500-entry in-memory ring is not an audit trail for a machine you cannot see.
/// </summary>
public sealed record AuditEntry
{
    /// <summary>When the call was forwarded.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The hub-assigned id of the client the call targeted.</summary>
    public required string ClientId { get; init; }

    /// <summary>The tool that was invoked, or a short description for non-invoke events.</summary>
    public required string ToolName { get; init; }

    /// <summary>The call outcome.</summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>An error message when <see cref="Outcome"/> is <see cref="AuditOutcome.Error"/>.</summary>
    public string? Error { get; init; }

    /// <summary>What kind of event this is. Defaults to <see cref="AuditKind.Invoke"/>.</summary>
    public AuditKind Kind { get; init; } = AuditKind.Invoke;

    /// <summary>The remote machine involved, when there is one.</summary>
    public string? Host { get; init; }

    /// <summary>How the client is attached, when known.</summary>
    public ClientTransport? Transport { get; init; }

    /// <summary>
    /// The agent session that caused this entry (e.g. <c>claude-code-2</c>), when one did.
    /// </summary>
    /// <remarks>
    /// Several agents share one hub, so "a click was sent to myapp#1" stopped being a
    /// complete answer: the operator also needs to know which agent sent it. Null for events
    /// the hub raised itself (a client attaching, the watchdog dropping one).
    /// </remarks>
    public string? Agent { get; init; }

    /// <summary>A short, human-readable one-liner for the tray log.</summary>
    public string Summary
    {
        get
        {
            var t = TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
            // Remote entries are marked so an operator scanning the log can tell at a glance
            // which lines came from another machine.
            var where = Host is { Length: > 0 } host ? $"@{host} " : string.Empty;
            var who = Agent is { Length: > 0 } agent ? $" [{agent}]" : string.Empty;

            if (Kind != AuditKind.Invoke)
            {
                var detail = Error is { Length: > 0 } e ? $"  — {e}" : string.Empty;
                return $"{t}  [{Kind.ToString().ToLowerInvariant()}]{who} {where}{ToolName}{detail}";
            }

            return Outcome switch
            {
                AuditOutcome.Started => $"{t}  →{who} {where}{ClientId}  {ToolName}",
                AuditOutcome.Ok => $"{t}  ✓{who} {where}{ClientId}  {ToolName}",
                AuditOutcome.Error => $"{t}  ✗{who} {where}{ClientId}  {ToolName}  — {Error}",
                _ => $"{t} {who} {where}{ClientId}  {ToolName}",
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

    private volatile IAuditSink? _sink;

    /// <summary>Creates a log keeping at most <paramref name="capacity"/> recent entries.</summary>
    public HubAuditLog(int capacity = 500)
    {
        _capacity = Math.Max(16, capacity);
    }

    /// <summary>
    /// An optional durable sink every entry is also written to. Null (the default) keeps the
    /// log purely in memory, which is all a same-machine-only hub has ever needed.
    /// </summary>
    /// <remarks>
    /// Set when remote access is enabled. A ring buffer that a busy session overwrites within
    /// minutes cannot answer "what did that machine do last Tuesday", and that is precisely
    /// the question an audit trail exists for once the client is somewhere you cannot see.
    /// </remarks>
    public IAuditSink? Sink
    {
        get => _sink;
        set => _sink = value;
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

        // A failing sink must never break the thing it is auditing. Losing a line to a full
        // disk is bad; refusing a tool call because of one would be worse.
        try { _sink?.Write(entry); } catch { /* best effort */ }

        EntryAdded?.Invoke(this, entry);
    }
}

/// <summary>A durable destination for audit entries.</summary>
public interface IAuditSink
{
    /// <summary>
    /// Persists one entry. Must not throw for ordinary failures and must not block for long —
    /// it is called on the broker thread, inline with tool dispatch.
    /// </summary>
    void Write(AuditEntry entry);
}
