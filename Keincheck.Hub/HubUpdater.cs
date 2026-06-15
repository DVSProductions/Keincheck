using Velopack;
using Velopack.Sources;

namespace Keincheck.Hub;

/// <summary>
/// Background auto-updater for an <i>installed</i> hub. Periodically polls the configured
/// GitHub release feed; when a newer version is published it downloads it, and then applies
/// it and restarts the daemon — but ONLY while no client is connected, so an in-progress
/// AI session is never interrupted. Checking and downloading happen in the background
/// regardless of client activity; only the apply-and-restart waits for an idle hub.
/// </summary>
/// <remarks>
/// <para>This type is pure orchestration: the actual "check + download" and "apply + restart"
/// operations are injected, so the idle-gating policy is unit-testable without the network or
/// a real install. <see cref="CreateGithub"/> wires the Velopack implementations and returns
/// <c>null</c> when the process is not a Velopack install (a dev build has nothing to update).</para>
/// <para>Threading: the poll loop runs on a background task; the idle-apply is also triggered
/// from the broker's <see cref="IClientBroker.ClientDown"/> event (an arbitrary thread). An
/// <see cref="Interlocked"/> latch guarantees the terminal apply fires at most once.</para>
/// </remarks>
public sealed class HubUpdater : IAsyncDisposable
{
    private readonly IClientBroker _broker;
    private readonly Func<CancellationToken, Task<string?>> _checkAndDownload;
    private readonly Action _applyAndRestart;
    private readonly TimeSpan _interval;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _gate = new();
    private bool _hasStaged;          // a newer release is downloaded and waiting for an idle hub
    private string? _stagedVersion;   // its version, for logging / status
    private int _applying;            // 0/1 latch so the terminal apply happens exactly once
    private Task? _loop;

    /// <param name="broker">Counts connected clients (apply only at zero) and signals when one drops.</param>
    /// <param name="checkAndDownload">
    /// Checks the feed and, if a newer release exists, downloads it; returns the staged version
    /// string, or <c>null</c> when already up to date.
    /// </param>
    /// <param name="applyAndRestart">The terminal "apply the staged update and restart" action.</param>
    /// <param name="interval">How often to poll for a newer release.</param>
    /// <param name="log">Sink for human-readable status lines. Optional.</param>
    public HubUpdater(
        IClientBroker broker,
        Func<CancellationToken, Task<string?>> checkAndDownload,
        Action applyAndRestart,
        TimeSpan interval,
        Action<string>? log = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _checkAndDownload = checkAndDownload ?? throw new ArgumentNullException(nameof(checkAndDownload));
        _applyAndRestart = applyAndRestart ?? throw new ArgumentNullException(nameof(applyAndRestart));
        _interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromHours(1);
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Wires a GitHub-backed updater for <paramref name="repoUrl"/>. Returns <c>null</c> when the
    /// current process is not a Velopack install (e.g. a dev build) — there is nothing to update.
    /// </summary>
    public static HubUpdater? CreateGithub(
        IClientBroker broker, string repoUrl, TimeSpan interval, Action<string>? log = null)
    {
        var manager = new UpdateManager(new GithubSource(repoUrl, accessToken: null, prerelease: false));
        if (!manager.IsInstalled)
        {
            log?.Invoke("auto-update inactive: not a Velopack install (dev build has nothing to update)");
            return null;
        }

        // Capture the UpdateInfo across check->apply via this closure so the orchestrator never
        // needs to know the Velopack types.
        UpdateInfo? pending = null;

        async Task<string?> CheckAndDownload(CancellationToken ct)
        {
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
                return null; // up to date
            await manager.DownloadUpdatesAsync(info, cancelToken: ct).ConfigureAwait(false);
            pending = info;
            return info.TargetFullRelease.Version.ToString();
        }

        void ApplyAndRestart()
        {
            if (pending is not null)
                manager.ApplyUpdatesAndRestart(pending.TargetFullRelease); // exits + relaunches
        }

        return new HubUpdater(broker, CheckAndDownload, ApplyAndRestart, interval, log);
    }

    /// <summary>True once a newer release is downloaded and only waiting for an idle hub.</summary>
    public bool UpdatePending { get { lock (_gate) return _hasStaged; } }

    /// <summary>The staged release's version, or <c>null</c> when none is pending.</summary>
    public string? PendingVersion { get { lock (_gate) return _stagedVersion; } }

    /// <summary>Starts the background poll loop and the idle-apply trigger. Call once.</summary>
    public void Start()
    {
        // Apply a staged update the moment the hub goes idle (the last client drops).
        _broker.ClientDown += OnClientDown;
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _log("auto-update active");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckNowAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log($"update check failed: {ex.Message}");
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Polls once: if no update is already staged, checks the feed and downloads a newer release;
    /// then applies it if the hub is idle. Returns whether an update is staged afterwards. Exposed
    /// so a tray "check now" affordance (or a test) can drive a deterministic single cycle.
    /// </summary>
    public async Task<bool> CheckNowAsync(CancellationToken ct = default)
    {
        if (!UpdatePending)
        {
            var version = await _checkAndDownload(ct).ConfigureAwait(false);
            if (version is not null)
            {
                lock (_gate)
                {
                    _hasStaged = true;
                    _stagedVersion = version;
                }
                _log($"update {version} downloaded; will apply when no client is connected");
            }
        }

        TryApplyIfIdle();
        return UpdatePending;
    }

    private void OnClientDown(object? sender, ClientInfo e) => TryApplyIfIdle();

    /// <summary>Applies a staged update iff the hub is idle (zero connected clients). Terminal.</summary>
    private void TryApplyIfIdle()
    {
        lock (_gate)
        {
            if (!_hasStaged)
                return;
        }

        if (_broker.ListClients().Count > 0)
            return; // a client is connected — keep the staged update and wait for it to drop

        if (Interlocked.Exchange(ref _applying, 1) != 0)
            return; // already applying (the apply is terminal; guard against a double trigger)

        _log($"no clients connected — applying staged update {PendingVersion} and restarting the hub");
        _applyAndRestart();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _broker.ClientDown -= OnClientDown;
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* shutting down */ }
        }
        _cts.Dispose();
    }
}
