using System.Diagnostics;
using Keincheck.Protocol;

namespace Keincheck.Connect;

/// <summary>
/// Mechanic #3 (shim) — the stdio MCP endpoint the AI's MCP client spawns. It
/// ensures a hub is running (launching the hub exe if absent), connects to the hub's
/// MCP-over-pipe endpoint, then byte-pumps in both directions: the AI's
/// <c>stdin → pipe</c> and <c>pipe → stdout</c>. The hub runs the actual MCP server
/// over the pipe stream, so this shim is transport-only — it never parses MCP.
/// </summary>
/// <remarks>
/// <para>All diagnostics go to <c>stderr</c>; <c>stdout</c> is reserved exclusively for
/// the MCP byte stream so the AI's parser is never corrupted.</para>
/// <para>The shim is intentionally dependency-light: it references only
/// <c>Keincheck.Protocol</c> (pipe names + single-instance mutex + pipe transport) and
/// the BCL. There is no MCP SDK reference — the bytes flow through untouched.</para>
/// </remarks>
public static class Program
{
    /// <summary>Default upper bound for "is the hub up and its MCP pipe reachable".</summary>
    private static readonly TimeSpan HubReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the child hub process to grab its single-instance mutex.</summary>
    private static readonly TimeSpan HubLaunchTimeout = TimeSpan.FromSeconds(15);

    public static async Task<int> Main(string[] args)
    {
        if (WantsHelp(args))
        {
            PrintUsage();
            return 0;
        }

        var pipeName = ResolveArg(args, "--pipe") ?? PipeNames.McpSessionPipe("default");
        var hubPath = ResolveArg(args, "--hub-exe");

        // One cancellation source drives the whole shim: Ctrl-C / SIGTERM, stdin EOF,
        // or a pipe drop all funnel into here so both pump directions tear down together.
        using var lifetime = new CancellationTokenSource();
        HookShutdownSignals(lifetime);

        try
        {
            await EnsureHubRunningAsync(hubPath, pipeName, lifetime.Token).ConfigureAwait(false);

            // ConnectAsync retries/backs off until the hub's MCP pipe is actually
            // listening — this (not the mutex) is the real "endpoint reachable" gate.
            await using var channel = await PipeTransport.ConnectAsync(
                pipeName, HubReadyTimeout, lifetime.Token).ConfigureAwait(false);

            Log($"connected to hub on pipe '{pipeName}'");
            await PumpAsync(channel.Stream, lifetime.Token).ConfigureAwait(false);
            Log("session ended");
            return 0;
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown (stdin EOF or Ctrl-C) — not an error for the AI client.
            Log("shutting down");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"fatal: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Bidirectional copy between the process's stdio and the hub pipe. The MCP framing
    /// is the SDK's newline-delimited JSON over the stream; we pass bytes through
    /// untouched. Whichever direction ends first (the AI closing stdin, or the hub
    /// dropping the pipe) tears the other down so the process exits promptly instead of
    /// leaking a half-open copy.
    /// </summary>
    private static async Task PumpAsync(Stream pipe, CancellationToken outer)
    {
        await using var stdin = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();

        // A nested source so either copy completing (or the outer lifetime cancelling)
        // signals the other to stop. Both copies observe the SAME token.
        using var pump = CancellationTokenSource.CreateLinkedTokenSource(outer);

        // stdin -> pipe : ends when the AI closes stdin (EOF) => session over.
        var toHub = CopyThenCancelAsync(stdin, pipe, pump, "stdin->pipe");
        // pipe -> stdout : ends when the hub drops the pipe => session over.
        var toAi = CopyThenCancelAsync(pipe, stdout, pump, "pipe->stdout");

        await Task.WhenAll(toHub, toAi).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies <paramref name="from"/> to <paramref name="to"/> until EOF, then signals
    /// <paramref name="pump"/> so the opposite direction also unwinds. Cancellation and
    /// the inevitable pipe/stream-closed faults during teardown are swallowed — they are
    /// the normal way a duplex copy ends, not failures to report.
    /// </summary>
    private static async Task CopyThenCancelAsync(
        Stream from, Stream to, CancellationTokenSource pump, string label)
    {
        try
        {
            await from.CopyToAsync(to, pump.Token).ConfigureAwait(false);
            Log($"{label} reached EOF");
        }
        catch (OperationCanceledException)
        {
            // The other direction ended first and cancelled us — expected.
        }
        catch (Exception ex)
        {
            // Pipe closed / stream disposed mid-copy during teardown — expected on drop.
            Log($"{label} ended: {ex.Message}");
        }
        finally
        {
            // Tell the opposite copy to stop; harmless if already cancelled/disposed.
            try { pump.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Returns once a hub is up and (best-effort) about to be reachable. If the per-user
    /// single-instance mutex already exists a hub is running; otherwise this launches the
    /// hub exe and waits for it to claim the mutex. Final reachability of the MCP pipe is
    /// proven by the caller's <see cref="PipeTransport.ConnectAsync"/> retry loop.
    /// </summary>
    private static async Task EnsureHubRunningAsync(string? hubPath, string pipeName, CancellationToken ct)
    {
        if (IsHubRunning())
        {
            Log("hub already running");
            return;
        }

        var exe = hubPath ?? ResolveHubExe();
        if (exe is null || !File.Exists(exe))
            throw new FileNotFoundException(
                "Hub is not running and the hub executable was not found. " +
                "Pass --hub-exe <path> or set KEINCHECK_HUB_EXE.", exe ?? "(null)");

        Log($"launching hub: {exe}");
        LaunchHub(exe);

        // Wait for the hub to grab its single-instance mutex (process is up). The pipe
        // may still be a beat behind — ConnectAsync's backoff covers that final gap.
        var deadline = DateTime.UtcNow + HubLaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (IsHubRunning())
            {
                Log("hub is up");
                return;
            }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"Hub did not start within {HubLaunchTimeout.TotalSeconds:0}s.");
    }

    /// <summary>
    /// Launches the hub as a fully detached process so it survives this shim exiting and
    /// never inherits/writes to the shim's stdio (stdout is MCP-only). The hub elects
    /// itself single-instance via its mutex, so a redundant launch is harmless.
    /// </summary>
    private static void LaunchHub(string exe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Do NOT redirect the child's stdout into ours — it must never reach the AI.
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };

        using var child = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start hub process '{exe}'.");
        // We do not keep a handle: the hub is a daemon and outlives the shim.
    }

    /// <summary>
    /// True if the hub's per-user single-instance mutex already exists (the hub holds it
    /// for its lifetime). We open without owning; success means a hub is up.
    /// </summary>
    private static bool IsHubRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(PipeNames.SingleInstanceMutex, out var existing))
            {
                existing.Dispose();
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but is owned by a session we can't open by ACL — still
            // means a hub is running for this user scope.
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Name not found => no hub.
        }
        return false;
    }

    /// <summary>
    /// Resolves the hub executable. Order:
    /// <list type="number">
    ///   <item><c>KEINCHECK_HUB_EXE</c> environment override (explicit deployment path).</item>
    ///   <item>Co-located next to this shim (Velopack install layout).</item>
    ///   <item>Dev fallback: the sibling <c>Keincheck.Hub/bin/{Config}/net*/</c> output,
    ///         discovered by walking up from the shim's bin dir to the solution root.</item>
    /// </list>
    /// </summary>
    private static string? ResolveHubExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KEINCHECK_HUB_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        // 1) Co-located deployment: hub exe sits next to this shim.
        //    TODO(velopack): the production install lays Connect + Hub side by side in the
        //    Velopack "current" directory; this branch covers that without code changes.
        var baseDir = AppContext.BaseDirectory;
        var coLocated = ProbeHubExe(baseDir);
        if (coLocated is not null)
            return coLocated;

        // 2) Dev fallback: find the sibling Hub project's build output. Connect's bin dir
        //    is .../Keincheck.Connect/bin/{Config}/net8.0/ ; the Hub's is
        //    .../Keincheck.Hub/bin/{Config}/net10.0/ . Walk up to the solution dir
        //    (the one containing both project folders) and probe the Hub output.
        return ProbeDevHubExe(baseDir);
    }

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for a sibling <c>Keincheck.Hub</c>
    /// project whose <c>bin</c> output contains a built hub exe. Prefers a build config
    /// matching this shim's own (Debug/Release) and the same kind of folder depth.
    /// </summary>
    private static string? ProbeDevHubExe(string startDir)
    {
        // From .../Keincheck.Connect/bin/{Config}/net8.0 climb until we find a directory
        // that has a sibling "Keincheck.Hub" folder (the solution root).
        var dir = new DirectoryInfo(startDir);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            var hubProject = Path.Combine(dir.FullName, "Keincheck.Hub");
            if (!Directory.Exists(hubProject))
                continue;

            var hubBin = Path.Combine(hubProject, "bin");
            if (!Directory.Exists(hubBin))
                return null;

            // Prefer this shim's own config (e.g. .../bin/Debug/...), else any config.
            var preferredConfig = CurrentBuildConfig();
            foreach (var config in OrderConfigs(hubBin, preferredConfig))
            {
                var configDir = Path.Combine(hubBin, config);
                if (!Directory.Exists(configDir))
                    continue;

                // net10.0 (or whatever TFM the hub targets) lives one level down.
                foreach (var tfmDir in Directory.EnumerateDirectories(configDir))
                {
                    var hit = ProbeHubExe(tfmDir);
                    if (hit is not null)
                        return hit;
                }
            }
            return null; // found the project but no usable build output
        }
        return null;
    }

    /// <summary>Returns the hub exe/dll path in <paramref name="dir"/> if present, else null.</summary>
    private static string? ProbeHubExe(string dir)
    {
        foreach (var name in new[] { "Keincheck.Hub.exe", "Keincheck.Hub" })
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>The build configuration this shim was compiled in (for dev-output preference).</summary>
    private static string CurrentBuildConfig()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    /// <summary>Orders the available build-config folders so the preferred one is tried first.</summary>
    private static IEnumerable<string> OrderConfigs(string hubBin, string preferred)
    {
        yield return preferred;
        foreach (var sub in Directory.EnumerateDirectories(hubBin))
        {
            var name = Path.GetFileName(sub);
            if (!string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
                yield return name;
        }
    }

    /// <summary>
    /// Wires Ctrl-C / Ctrl-Break and process-exit into <paramref name="lifetime"/> so a
    /// signal triggers the same graceful teardown as stdin EOF.
    /// </summary>
    private static void HookShutdownSignals(CancellationTokenSource lifetime)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // we handle teardown ourselves; don't hard-kill mid-frame
            Log("cancel signal received");
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        };
    }

    private static bool WantsHelp(string[] args)
    {
        foreach (var a in args)
            if (a is "-h" or "--help" or "/?" or "/h")
                return true;
        return false;
    }

    private static void PrintUsage()
    {
        // Usage prints to stderr too: stdout must stay MCP-only even for --help.
        Console.Error.WriteLine(
            """
            keincheck-connect — stdio<->hub MCP bridge

            Usage:
              keincheck-connect [--pipe <name>] [--hub-exe <path>]

            Options:
              --pipe <name>     Hub MCP pipe name (default: per-user "default" session pipe).
              --hub-exe <path>  Hub executable to launch if no hub is running.
                                Falls back to KEINCHECK_HUB_EXE, a co-located hub,
                                or the dev build output.
              -h, --help        Show this help.

            All diagnostics go to stderr; stdout carries only the MCP byte stream.
            """);
    }

    private static string? ResolveArg(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void Log(string message)
        => Console.Error.WriteLine($"[keincheck-connect] {message}");
}
