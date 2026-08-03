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

    private StreamWriter? _writer;
    private DateOnly _openDay;
    private int _sinceLastPrune;
    private int _disposed;

    public JsonlAuditSink(string? directory = null, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        _directory = directory ?? DefaultDirectory();
        _maxTotalBytes = maxTotalBytes > 0 ? maxTotalBytes : DefaultMaxTotalBytes;
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
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_gate)
        {
            var day = DateOnly.FromDateTime(entry.TimestampUtc.UtcDateTime);
            if (_writer is null || day != _openDay)
                Reopen(day);

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
        System.IO.Directory.CreateDirectory(_directory);
        _writer = new StreamWriter(
            new FileStream(
                Path.Combine(_directory, $"{day:yyyy-MM-dd}.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _openDay = day;
        Prune();
    }

    /// <summary>Deletes the oldest day files until the directory fits the budget.</summary>
    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_directory).GetFiles("*.jsonl")
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();

            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= _maxTotalBytes || files.Count <= 1)
                    break;
                // Never delete the file currently open for writing.
                if (file.Name == $"{_openDay:yyyy-MM-dd}.jsonl")
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
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
