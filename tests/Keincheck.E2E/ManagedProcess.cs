using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Keincheck.E2E;

/// <summary>
/// A child process the suite owns: stdio streamed to a log file for CI artifacts and
/// mirrored into a bounded in-memory buffer the assertions can read, exit tracked, and
/// killed tree-wide on dispose.
/// </summary>
/// <remarks>
/// <para>The hub is a <c>WinExe</c>, but everything it reports — <c>Program.Main</c>,
/// <see langword="RemoteAccess"/>, and the updater — goes through
/// <c>Console.Error.WriteLine</c>, so with <c>UseShellExecute=false</c> and redirection we
/// capture all of it. This is the reason the suite starts the hub itself instead of
/// letting the installer or the shim do it: the shim deliberately launches the hub
/// detached with no redirection, so that hub's output can never reach the AI's stdout —
/// which also means it can never reach a CI artifact.</para>
/// <para>Processes started by the hub on our behalf (<c>hub_launch_client</c> /
/// <c>hub_restart_client</c>) use <c>UseShellExecute=true</c> inside the broker, so their
/// stdio is unreachable. <see cref="Adopt"/> tracks those by pid for teardown only.</para>
/// </remarks>
public sealed class ManagedProcess : IDisposable
{
    private const int BufferedLines = 500;

    private readonly Process _process;
    private readonly StreamWriter? _log;
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly bool _capturesOutput;

    public string Name { get; }
    public string? LogPath { get; }

    private ManagedProcess(string name, Process process, StreamWriter? log, string? logPath, bool capturesOutput)
    {
        Name = name;
        _process = process;
        _log = log;
        LogPath = logPath;
        _capturesOutput = capturesOutput;
    }

    /// <summary>Starts <paramref name="exePath"/> with its stdio captured to the artifacts directory.</summary>
    public static ManagedProcess Start(
        string name,
        string exePath,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"{name}: executable not found.", exePath);

        var logPath = Path.Combine(E2EEnvironment.ArtifactsDirectory, $"{name}.log");
        var log = new StreamWriter(
            new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            Encoding.UTF8)
        { AutoFlush = true };

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var managed = new ManagedProcess(name, process, log, logPath, capturesOutput: true);

        process.OutputDataReceived += (_, e) => managed.Record("out", e.Data);
        process.ErrorDataReceived += (_, e) => managed.Record("err", e.Data);

        if (!process.Start())
            throw new InvalidOperationException($"{name}: failed to start '{exePath}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        managed.Record("e2e", $"started pid {process.Id}: {exePath}");
        return managed;
    }

    /// <summary>
    /// Tracks an already-running process by pid, for teardown only. Used for the demo
    /// instances the hub launches itself, whose stdio the broker gave away.
    /// </summary>
    public static ManagedProcess? Adopt(string name, int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            process.EnableRaisingEvents = true;
            return new ManagedProcess(name, process, log: null, logPath: null, capturesOutput: false);
        }
        catch (ArgumentException)
        {
            return null; // already gone
        }
    }

    private void Record(string stream, string? line)
    {
        if (line is null)
            return;

        var entry = $"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {stream}| {line}";
        _log?.WriteLine(entry);

        _recent.Enqueue(line);
        while (_recent.Count > BufferedLines && _recent.TryDequeue(out _)) { }
    }

    /// <summary>Writes a harness-authored line into this process's log, for narrative context.</summary>
    public void Note(string message) => Record("e2e", message);

    public int Id => _process.Id;

    public bool HasExited
    {
        get
        {
            try { _process.Refresh(); return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public int? ExitCode
    {
        get
        {
            try { return _process.HasExited ? _process.ExitCode : null; }
            catch { return null; }
        }
    }

    /// <summary>The most recent captured lines, oldest first.</summary>
    public IReadOnlyList<string> Tail(int count = 40)
    {
        var all = _recent.ToArray();
        return all.Length <= count ? all : all[^count..];
    }

    /// <summary>
    /// CLR crash banners only. A looser scan would flag the hub's own benign lines —
    /// <c>[keincheck-hub:update] update check failed: …</c> is a normal offline message,
    /// not a crash — and a flapping crash detector is worse than none at all.
    /// </summary>
    private static readonly string[] CrashBanners =
    [
        "Unhandled exception",
        "Fatal error.",
        "Stack overflow",
    ];

    /// <summary>Returns the crash banner lines seen in this process's output, if any.</summary>
    public IReadOnlyList<string> CrashBanners_Seen() =>
        _recent.Where(line => CrashBanners.Any(b => line.Contains(b, StringComparison.OrdinalIgnoreCase)))
               .ToArray();

    /// <summary>A one-line summary plus recent output, for a failure message.</summary>
    public string Describe(int tailLines = 40)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Name}: pid {SafeId()}, exited={HasExited}, exitCode={ExitCode?.ToString() ?? "n/a"}");
        if (!_capturesOutput)
        {
            sb.AppendLine("  (stdio unavailable — hub-launched process)");
            return sb.ToString();
        }
        foreach (var line in Tail(tailLines))
            sb.AppendLine($"  {line}");
        return sb.ToString();
    }

    private string SafeId()
    {
        try { return _process.Id.ToString(); } catch { return "?"; }
    }

    public void Kill()
    {
        try
        {
            if (!HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Record("e2e", $"kill failed: {ex.Message}");
        }

        try { _process.WaitForExit(10_000); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        Kill();
        _log?.Dispose();
        _process.Dispose();
    }
}
