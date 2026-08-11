using System.Net;
using System.Text;
using Microsoft.Playwright;

namespace Keincheck.E2E;

/// <summary>
/// Serves the published WebAssembly bundle and drives a real headless browser at it.
/// </summary>
/// <remarks>
/// <para>
/// The port is <b>not</b> free choice. The browser demo is built with
/// <c>KeincheckWebSocketOrigin=http://localhost:5000</c>, the hub allowlists that exact origin,
/// and a page served from any other port is refused with 403 — which would look like a
/// transport failure rather than the configuration mismatch it is.
/// </para>
/// <para>
/// The browser comes from Playwright rather than from the machine. A runner's preinstalled
/// browsers vary by image and change without notice, and <c>Process.Start("firefox")</c> gives
/// no headless control and no reliable way to close what it opened.
/// </para>
/// </remarks>
public sealed class BrowserRig : IAsyncDisposable
{
    /// <summary>The origin the demo is enrolled against. Must match the build property.</summary>
    public const int Port = 5000;

    public static string Origin => $"http://localhost:{Port}";

    private readonly HttpListener _listener = new();
    private readonly string _root;
    private CancellationTokenSource? _serving;
    private Task? _serveLoop;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private BrowserRig(string root) => _root = root;

    /// <summary>The console output the page produced, for failure diagnosis.</summary>
    public List<string> ConsoleLog { get; } = [];

    /// <summary>
    /// Publishes nothing and builds nothing: the bundle is produced by CI (or by hand) and
    /// pointed at through <see cref="E2EEnvironment.BrowserBundleVar"/>. Building wasm from
    /// inside a test would need the wasm-tools workload at test time and would hide a build
    /// failure inside a test failure.
    /// </summary>
    public static async Task<BrowserRig> StartAsync(string bundleDirectory)
    {
        if (!Directory.Exists(bundleDirectory))
            throw new DirectoryNotFoundException($"No WebAssembly bundle at '{bundleDirectory}'.");
        if (!File.Exists(Path.Combine(bundleDirectory, "index.html")))
            throw new FileNotFoundException($"'{bundleDirectory}' has no index.html; it is not an AppBundle.");

        var rig = new BrowserRig(bundleDirectory);
        rig.StartServer();
        await rig.StartBrowserAsync().ConfigureAwait(false);
        return rig;
    }

    // ---- static server ----------------------------------------------------

    private void StartServer()
    {
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // "The process cannot access the file because it is being used by another process"
            // is what HttpListener says when the port is taken, which names neither the port
            // nor the fact that it cannot be changed without breaking the origin allowlist.
            throw new InvalidOperationException(
                $"Could not bind {Origin}: something else is already listening on port {Port}. "
                + "The port is fixed because the demo is enrolled against that exact origin and "
                + "the hub refuses any other with 403, so free the port rather than moving it.",
                ex);
        }
        _serving = new CancellationTokenSource();
        _serveLoop = Task.Run(() => ServeAsync(_serving.Token));
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; } // listener stopped

            try
            {
                var rel = context.Request.Url!.AbsolutePath.TrimStart('/');
                if (rel.Length == 0)
                    rel = "index.html";

                // Contain the served path: a request for ../../secrets must not escape the
                // bundle, even in a test that only ever serves to its own browser.
                var full = Path.GetFullPath(Path.Combine(_root, rel));
                if (!full.StartsWith(Path.GetFullPath(_root), StringComparison.Ordinal) || !File.Exists(full))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                context.Response.ContentType = ContentTypeFor(full);
                // .wasm MUST be application/wasm or instantiateStreaming refuses it.
                context.Response.Headers["Cache-Control"] = "no-store";
                var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                context.Response.Close();
            }
            catch
            {
                try { context.Response.Abort(); } catch { /* client went away */ }
            }
        }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".wasm" => "application/wasm",
        ".css" => "text/css; charset=utf-8",
        ".dat" or ".blat" => "application/octet-stream",
        _ => "application/octet-stream",
    };

    // ---- browser ----------------------------------------------------------

    private async Task StartBrowserAsync()
    {
        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);

        // Chromium by default; KEINCHECK_E2E_BROWSER=firefox switches, because Avalonia renders
        // through WebGL and a headless GPU stack is the most likely thing to differ between
        // engines. Being able to swap without a code change is what makes that diagnosable.
        var which = Environment.GetEnvironmentVariable(E2EEnvironment.BrowserVar);
        var type = string.Equals(which, "firefox", StringComparison.OrdinalIgnoreCase)
            ? _playwright.Firefox
            : _playwright.Chromium;

        _browser = await type.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // SwiftShader: headless Chromium has no GPU, and Avalonia's renderer needs WebGL to
            // come up at all. Without it the page loads and never paints, which reads exactly
            // like the app failing to start.
            Args = type == _playwright.Chromium
                ? ["--use-gl=swiftshader", "--enable-unsafe-swiftshader", "--no-sandbox"]
                : null,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the demo and returns once the page has loaded. Attachment is asserted through the
    /// hub, not here — the browser's own idea of "loaded" says nothing about whether a client
    /// registered.
    /// </summary>
    public async Task<IPage> OpenAsync()
    {
        var page = await _browser!.NewPageAsync().ConfigureAwait(false);

        page.Console += (_, e) => ConsoleLog.Add($"[{e.Type}] {e.Text}");
        page.PageError += (_, e) => ConsoleLog.Add($"[pageerror] {e}");

        await page.GotoAsync(Origin, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 120_000, // a cold wasm boot downloads and JITs the whole runtime
        }).ConfigureAwait(false);

        return page;
    }

    /// <summary>The page console, for attaching to a failure message.</summary>
    public string DescribeConsole() => ConsoleLog.Count == 0
        ? "(the page logged nothing)"
        : string.Join(Environment.NewLine, ConsoleLog.TakeLast(40));

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.CloseAsync().ConfigureAwait(false);
        _playwright?.Dispose();

        _serving?.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        if (_serveLoop is not null)
        {
            try { await _serveLoop.ConfigureAwait(false); } catch { /* shutting down */ }
        }
        _listener.Close();
    }
}
