using System.Text.Json;

namespace Keincheck.Hub;

/// <summary>
/// One captured step of a hub recording: a single <i>proxied</i> tool invocation that
/// flowed through <see cref="HubMcpServer"/> to a client, together with the client it
/// targeted, the arguments that were forwarded, and whether the client reported success.
/// </summary>
/// <remarks>
/// Meta-tools (and the record/replay/guide tools themselves) are never captured — only
/// the UI-driving tools the AI proxied to a client, so a recording replays as a clean
/// scenario without the bookkeeping calls that produced it.
/// </remarks>
public sealed record RecordedStep
{
    /// <summary>The hub-assigned id of the client this step was forwarded to.</summary>
    public required string ClientId { get; init; }

    /// <summary>The (unqualified) tool name that was invoked on the client.</summary>
    public required string ToolName { get; init; }

    /// <summary>The arguments forwarded to the tool (the post-resolve payload), or null.</summary>
    public JsonElement? ArgsJson { get; init; }

    /// <summary>True if the client reported a successful result for this step.</summary>
    public required bool Ok { get; init; }
}

/// <summary>
/// A thread-safe holder for the hub's <b>active recording</b>: an ordered list of the
/// proxied tool calls captured since <see cref="Start"/>. <see cref="HubMcpServer"/> owns
/// a single instance, appends a <see cref="RecordedStep"/> after each proxied invoke while
/// recording is active, and reads the buffer back for replay/export.
/// </summary>
/// <remarks>
/// All access is guarded by an internal lock because steps are appended from the ASP.NET
/// request thread pool (MCP call handlers) while the meta-tools that read the buffer may
/// run concurrently on a different request. Snapshots return defensive copies so callers
/// can enumerate without holding the lock.
/// </remarks>
public sealed class HubRecorder
{
    private readonly object _gate = new();
    private readonly List<RecordedStep> _steps = new();
    private bool _recording;
    private string? _name;

    /// <summary>True while a recording is in progress.</summary>
    public bool IsRecording
    {
        get { lock (_gate) return _recording; }
    }

    /// <summary>The optional name given to the active (or most recent) recording.</summary>
    public string? Name
    {
        get { lock (_gate) return _name; }
    }

    /// <summary>The number of steps currently buffered.</summary>
    public int Count
    {
        get { lock (_gate) return _steps.Count; }
    }

    /// <summary>
    /// Begins a fresh recording: clears any buffered steps and arms capture. An optional
    /// <paramref name="name"/> is stored for status/export labelling.
    /// </summary>
    public void Start(string? name)
    {
        lock (_gate)
        {
            _steps.Clear();
            _recording = true;
            _name = name;
        }
    }

    /// <summary>
    /// Stops capture (the buffer is retained so it can still be replayed/exported) and
    /// returns the number of steps that were recorded.
    /// </summary>
    public int Stop()
    {
        lock (_gate)
        {
            _recording = false;
            return _steps.Count;
        }
    }

    /// <summary>
    /// Appends a step iff a recording is active. No-ops otherwise, so the caller need not
    /// re-check <see cref="IsRecording"/> under its own race window.
    /// </summary>
    public void Capture(RecordedStep step)
    {
        lock (_gate)
        {
            if (_recording)
                _steps.Add(step);
        }
    }

    /// <summary>A defensive, point-in-time copy of the buffered steps (oldest first).</summary>
    public IReadOnlyList<RecordedStep> Snapshot()
    {
        lock (_gate)
            return _steps.ToList();
    }
}
