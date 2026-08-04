using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keincheck.Hub.Remote;

/// <summary>
/// Appends audit entries to a day-rolled JSON-lines file under
/// <c>%APPDATA%\Keincheck\audit</c>, keeping the directory under a size budget.
/// </summary>
/// <remarks>
/// <para>
/// JSON lines rather than a database or a structured log framework: it is greppable, it is
/// appendable without a read-modify-write, a truncated last line costs you one record instead
/// of the file, and it adds no dependency to a hub that currently has none for this.
/// </para>
/// <para>
/// The size cap is a real requirement, not tidiness. This runs unattended for months on a
/// machine nobody logs into, and an audit trail that fills the disk has caused a worse
/// incident than the one it was recording.
/// </para>
/// </remarks>
public sealed class JsonlAuditSink : IAuditSink, IDisposable
{
    /// <summary>Total bytes of audit files to keep before deleting the oldest days.</summary>
    public const long DefaultMaxTotalBytes = 50L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxTotalBytes;

    /// <summary>
    /// The current file is rolled once it passes this, rather than only at midnight.
    /// </summary>
    /// <remarks>
    /// Without a size roll the budget could only ever bound <i>old</i> files: one busy or noisy
    /// day — a client stuck in a reconnect loop, say — would grow today's file past the total
    /// allowance and nothing could shrink it until the date changed. An audit trail that fills
    /// the disk causes a worse incident than the one it was recording.
    /// </remarks>
    public const long DefaultMaxFileBytes = 8L * 1024 * 1024;

    private readonly long _maxFileBytes;
    private StreamWriter? _writer;
    private FileStream? _stream;
    private DateOnly _openDay;
    private int _rollIndex;
    private int _sinceLastPrune;
    private int _disposed;

    public JsonlAuditSink(
        string? directory = null,
        long maxTotalBytes = DefaultMaxTotalBytes,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        _directory = directory ?? DefaultDirectory();
        _maxTotalBytes = maxTotalBytes > 0 ? maxTotalBytes : DefaultMaxTotalBytes;
        _maxFileBytes = maxFileBytes > 0 ? maxFileBytes : DefaultMaxFileBytes;
    }

    /// <summary>The default location: <c>%APPDATA%\Keincheck\audit</c>.</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keincheck", "audit");

    /// <summary>The folder entries are written to.</summary>
    public string Directory => _directory;

    /// <inheritdoc/>
    public void Write(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            // Checked INSIDE the lock: reading it outside let a Write racing Dispose re-enter
            // Reopen and create a FileStream nobody would ever close.
            if (_disposed != 0)
                return;

            var day = DateOnly.FromDateTime(entry.TimestampUtc.UtcDateTime);
            if (_writer is null || day != _openDay)
            {
                _rollIndex = 0;
                Reopen(day);
            }
            else if (_stream is not null && _stream.Length >= _maxFileBytes)
            {
                _rollIndex++;
                Reopen(day);
            }

            _writer!.WriteLine(JsonSerializer.Serialize(new Record
            {
                Timestamp = entry.TimestampUtc,
                Kind = entry.Kind.ToString(),
                ClientId = entry.ClientId,
                Host = entry.Host,
                Transport = entry.Transport?.ToString(),
                Tool = entry.ToolName,
                Outcome = entry.Outcome.ToString(),
                Error = entry.Error,
            }, Json));

            // Flushed per entry on purpose. An audit trail that loses the last few records
            // when the process is killed loses exactly the records that explain why.
            _writer.Flush();

            if (++_sinceLastPrune >= 200)
            {
                _sinceLastPrune = 0;
                Prune();
            }
        }
    }

    private void Reopen(DateOnly day)
    {
        _writer?.Dispose();
        _writer = null;
        _stream = null;

        System.IO.Directory.CreateDirectory(_directory);

        // On a fresh day, skip past any parts already on disk so a hub restarted several times
        // in one day appends rather than reopening a full part.
        if (_rollIndex == 0)
        {
            // FileInfo.Length THROWS for a missing file rather than reporting zero, so the
            // existence check has to come first — otherwise the very first write of the day
            // fails before the file is ever created.
            while (_rollIndex < 10_000)
            {
                var candidate = new FileInfo(PathFor(day, _rollIndex));
                if (!candidate.Exists || candidate.Length < _maxFileBytes)
                    break;
                _rollIndex++;
            }
        }

        // ReadWrite, not Read: with FileShare.Read the operating system refuses any reader
        // whose own share mode does not permit our write handle, so File.ReadAllText and
        // ordinary tools could not open TODAY's log while the hub was running — you could only
        // read yesterday's. An audit trail you cannot read during the incident is not much of
        // an audit trail.
        _stream = new FileStream(
            PathFor(day, _rollIndex), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _openDay = day;
        Prune();
    }

    /// <summary>Part 0 keeps the plain <c>yyyy-MM-dd.jsonl</c> name; later parts get a suffix.</summary>
    private string PathFor(DateOnly day, int part) => Path.Combine(
        _directory, part == 0 ? $"{day:yyyy-MM-dd}.jsonl" : $"{day:yyyy-MM-dd}.{part:000}.jsonl");

    /// <summary>Deletes the oldest day files until the directory fits the budget.</summary>
    private void Prune()
    {
        try
        {
            var openName = Path.GetFileName(PathFor(_openDay, _rollIndex));
            var files = new DirectoryInfo(_directory).GetFiles("*.jsonl")
                .OrderBy(f => f.Name, StringComparer.Ordinal)   // oldest day, then oldest part
                .ToList();

            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= _maxTotalBytes)
                    break;
                // Never delete the file currently open for writing.
                if (file.Name == openName)
                    continue;

                total -= file.Length;
                file.Delete();
            }
        }
        catch
        {
            // Pruning is housekeeping; failing at it must not stop the hub auditing.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _writer?.Dispose();
            _writer = null;
            _stream = null;
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private sealed class Record
    {
        [JsonPropertyName("ts")] public DateTimeOffset Timestamp { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
        [JsonPropertyName("host")] public string? Host { get; set; }
        [JsonPropertyName("transport")] public string? Transport { get; set; }
        [JsonPropertyName("tool")] public string? Tool { get; set; }
        [JsonPropertyName("outcome")] public string? Outcome { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}

/// <summary>Helpers for building the non-invoke audit entries.</summary>
internal static class RemoteAudit
{
    /// <summary>Builds an entry for a remote lifecycle event.</summary>
    public static AuditEntry Entry(
        AuditKind kind, string description, string? clientId = null, string? host = null,
        ClientTransport? transport = null, string? error = null) => new()
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ClientId = clientId ?? "-",
            ToolName = description,
            Outcome = error is null ? AuditOutcome.Ok : AuditOutcome.Error,
            Kind = kind,
            Host = host,
            Transport = transport,
            Error = error,
        };
}
