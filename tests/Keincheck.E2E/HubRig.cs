using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Keincheck.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol; // StreamClientTransport lives here, not in .Client
using Xunit;

namespace Keincheck.E2E;

/// <summary>
/// Owns the live hub process for the whole assembly: preflight, launch, readiness,
/// the three MCP transports, and the crash verdict at teardown.
/// </summary>
/// <remarks>
/// One rig per run, because there can only ever be one hub per user — the single-instance
/// mutex, the control pipe, and port 3100 are all fixed per-user names with no override.
/// </remarks>
public sealed class HubRig : IAsyncLifetime
{
    /// <summary>Loopback port <c>HubOptions.HttpPort</c> defaults to.</summary>
    public const int HttpPort = 3100;

    private readonly List<ManagedProcess> _children = [];
    private ManagedProcess? _hub;

    /// <summary>Directory holding <c>Keincheck.Hub.exe</c> and <c>keincheck-connect.exe</c>.</summary>
    public string HubDirectory { get; private set; } = "";

    public string HubExe => Path.Combine(HubDirectory, "Keincheck.Hub.exe");
    public string ConnectExe => Path.Combine(HubDirectory, "keincheck-connect.exe");

    /// <summary>The hub process this rig started. Null before <see cref="InitializeAsync"/>.</summary>
    public ManagedProcess Hub => _hub ?? throw new InvalidOperationException("The rig has no hub.");

    // ---- lifecycle --------------------------------------------------------

    public async Task InitializeAsync()
    {
        if (!E2EEnvironment.IsEnabled)
            return; // every fact is skipped; do not touch the machine

        HubDirectory = ResolveHubDirectory();
        await PreflightAsync();

        _hub = ManagedProcess.Start("hub", HubExe, HubDirectory);
        await WaitUntilReadyAsync(TimeSpan.FromSeconds(90));
    }

    public Task DisposeAsync()
    {
        if (_hub is null)
            return Task.CompletedTask;

        // The verdict must be taken BEFORE anything is killed, or "it survived" would be
        // a statement about our own kill order rather than about the hub.
        var failures = new List<string>();

        if (_hub.HasExited)
            failures.Add($"the hub exited during the run (code {_hub.ExitCode}).\n{_hub.Describe()}");

        foreach (var child in _children.Where(c => c.HasExited))
            child.Note("exited before teardown (may be intentional)");

        foreach (var process in _children.Prepend(_hub))
        {
            var banners = process.CrashBanners_Seen();
            if (banners.Count > 0)
                failures.Add($"{process.Name} logged a CLR crash banner:\n  {string.Join("\n  ", banners)}");
        }

        foreach (var child in _children)
            child.Dispose();
        _hub.Dispose();
        _children.Clear();

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        return Task.CompletedTask;
    }

    /// <summary>Registers a process for teardown (and for the crash verdict, if it has logs).</summary>
    public ManagedProcess Track(ManagedProcess process)
    {
        _children.Add(process);
        return process;
    }

    /// <summary>
    /// Registers a pid the hub started on our behalf. <c>hub_launch_client</c> and
    /// <c>hub_restart_client</c> go through <c>Process.Start</c> with
    /// <c>UseShellExecute = true</c>, so those instances are neither our children nor
    /// capturable — without this they would leak on a self-hosted runner.
    /// </summary>
    public void TrackLaunchedPid(string name, int pid)
    {
        if (ManagedProcess.Adopt(name, pid) is { } adopted)
            _children.Add(adopted);
    }

    // ---- resolution & preflight ------------------------------------------

    private static string ResolveHubDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(E2EEnvironment.HubDirVar);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Directory.Exists(configured))
                throw new DirectoryNotFoundException($"{E2EEnvironment.HubDirVar}='{configured}' does not exist.");
            return configured;
        }

        // Dev fallback, deliberately the same walk-up-and-probe shape as
        // Keincheck.Connect.Program.ProbeDevHubExe, so both fallbacks fail the same way
        // and get fixed together.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            var hubBin = Path.Combine(dir.FullName, "Keincheck.Hub", "bin");
            if (!Directory.Exists(hubBin))
                continue;

            foreach (var config in Directory.EnumerateDirectories(hubBin))
                foreach (var tfm in Directory.EnumerateDirectories(config))
                    if (File.Exists(Path.Combine(tfm, "Keincheck.Hub.exe")))
                        return tfm;
        }

        throw new DirectoryNotFoundException(
            $"No hub found. Set {E2EEnvironment.HubDirVar} to a directory containing " +
            "Keincheck.Hub.exe and keincheck-connect.exe (for a Velopack install that is " +
            "the 'current' subdirectory), or build Keincheck.Hub locally.");
    }

    private async Task PreflightAsync()
    {
        if (!File.Exists(HubExe))
            throw new FileNotFoundException("Hub executable missing from the configured directory.", HubExe);
        if (!File.Exists(ConnectExe))
            throw new FileNotFoundException(
                "keincheck-connect.exe is missing from the hub directory. The shim must ship " +
                "co-located with the hub — that is how the hub's 'Set up AI assistant' points " +
                "the client at it, and how the shim finds the hub to launch.", ConnectExe);

        // A hub may already be up: the Velopack installer can launch one, or a developer
        // may have left theirs running. Ours would then lose the single-instance election
        // and exit 0 instantly, and every later assertion would be silently measuring a
        // process whose output we never captured.
        if (IsHubRunning())
        {
            foreach (var stray in Process.GetProcessesByName("Keincheck.Hub"))
            {
                try { stray.Kill(entireProcessTree: true); stray.WaitForExit(10_000); }
                catch { /* raced us to exit */ }
                finally { stray.Dispose(); }
            }

            var clear = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline && IsHubRunning())
                    await Task.Delay(200);
            });
            await clear;

            if (IsHubRunning())
                throw new InvalidOperationException(
                    $"A hub still holds '{PipeNames.SingleInstanceMutex}' and could not be stopped. " +
                    "Quit it before running the E2E suite.");
        }

        // HubMcpServer.StartHttp has no try/catch around the Kestrel bind, so an occupied
        // 3100 throws out of Program.Main before Avalonia even starts. Detecting it here
        // turns an instant, unexplained death into one sentence.
        var listening = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(e => e.Port == HttpPort && (e.Address.Equals(IPAddress.Loopback) || e.Address.Equals(IPAddress.Any)));
        if (listening)
            throw new InvalidOperationException(
                $"Port {HttpPort} is already listening. The hub's Kestrel bind would throw and " +
                "take the whole daemon down. Free the port before running the E2E suite.");
    }

    /// <summary>True while some hub holds the per-user single-instance mutex.</summary>
    public static bool IsHubRunning()
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
            return true; // exists, just not openable by us
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // no hub
        }
        return false;
    }

    /// <summary>
    /// The shim's own two-stage readiness gate: the mutex proves the process is up, and a
    /// successful connect to the MCP pipe proves the endpoint is actually serving.
    /// </summary>
    private async Task WaitUntilReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!IsHubRunning())
        {
            // Watching for exit matters as much as watching for the mutex. Program.Main's
            // finally tears down the broker and the MCP servers if Avalonia throws, so a
            // UI failure kills the pipe too — and without this check that presents as a
            // silent 90-second timeout instead of an exit code and a stack trace.
            if (Hub.HasExited)
                throw new InvalidOperationException(
                    $"The hub exited with code {Hub.ExitCode} before claiming its mutex.\n{Hub.Describe()}");

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The hub never claimed '{PipeNames.SingleInstanceMutex}'.\n{Hub.Describe()}");

            await Task.Delay(200);
        }

        var remaining = deadline - DateTime.UtcNow;
        if (remaining < TimeSpan.FromSeconds(5))
            remaining = TimeSpan.FromSeconds(5);

        try
        {
            await using var probe = await PipeTransport.ConnectAsync(McpPipeName, remaining);
            Hub.Note($"ready: MCP pipe '{McpPipeName}' accepted a connection");
        }
        catch (Exception ex)
        {
            throw new TimeoutException(
                $"The hub is up but its MCP pipe '{McpPipeName}' never accepted: {ex.Message}\n{Hub.Describe()}");
        }
    }

    /// <summary>
    /// The MCP-over-pipe endpoint name. <c>Program.Main</c> never sets
    /// <c>HubOptions.PipeName</c> (only <c>BrokerOptions.PipeName</c>), so the listener
    /// falls through to the "default" session pipe.
    /// </summary>
    public static string McpPipeName => PipeNames.McpSessionPipe("default");

    // ---- transports -------------------------------------------------------

    /// <summary>
    /// Connects through <c>keincheck-connect.exe</c> over stdio — the exact path Claude
    /// Code and Claude Desktop take. <c>--hub-exe</c> and <c>KEINCHECK_HUB_EXE</c> are left
    /// unset on purpose so the shim has to resolve the hub by co-location, which is the
    /// branch the Velopack layout relies on and which nothing else exercises.
    /// </summary>
    public async Task<McpClient> ConnectViaShimAsync(CancellationToken ct = default)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "keincheck-connect",
            Command = ConnectExe,
            WorkingDirectory = HubDirectory,
            ShutdownTimeout = TimeSpan.FromSeconds(10),
            StandardErrorLines = line => Hub.Note($"shim| {line}"),
        });

        return await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, cancellationToken: ct);
    }

    /// <summary>
    /// Connects straight to the hub's MCP pipe. Buys a bisect: when the shim path fails,
    /// this says immediately whether the hub's endpoint or the shim is at fault.
    /// </summary>
    public async Task<(McpClient client, PipeChannel channel)> ConnectViaPipeAsync(CancellationToken ct = default)
    {
        var channel = await PipeTransport.ConnectAsync(McpPipeName, TimeSpan.FromSeconds(30), ct);
        var transport = new StreamClientTransport(
            serverInput: channel.Stream,
            serverOutput: channel.Stream,
            loggerFactory: null);

        var client = await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, cancellationToken: ct);
        return (client, channel);
    }

    /// <summary>
    /// Connects to the loopback HTTP endpoint. The only coverage <c>HubMcpServer.StartHttp</c>
    /// has anywhere — without it, a Kestrel/MapMcp regression ships silently.
    /// </summary>
    public async Task<McpClient> ConnectViaHttpAsync(CancellationToken ct = default)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "hub-http",
            Endpoint = new Uri($"http://127.0.0.1:{HttpPort}"),
        });

        return await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, cancellationToken: ct);
    }
}

/// <summary>The single collection every E2E fact joins, so they share one hub.</summary>
[CollectionDefinition(Name)]
public sealed class HubCollection : ICollectionFixture<HubRig>
{
    public const string Name = "keincheck-hub-e2e";
}
