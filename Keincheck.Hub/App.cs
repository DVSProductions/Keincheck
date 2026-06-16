using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Keincheck.Hub;

/// <summary>
/// The hub's Avalonia application: a tray icon plus a status window listing clients
/// (launch / restart / read-only / make-active), an audit log of AI tool calls, and the
/// "AI is driving X" banner. The daemon lives in the tray — closing the window hides it
/// rather than exiting. <see cref="Program"/> supplies the live broker.
/// </summary>
public sealed class App : Application
{
    private readonly PipeClientBroker _broker;
    private HubViewModel? _vm;
    private HubWindow? _window;
    private TrayIcon? _tray;

    public App(PipeClientBroker broker)
    {
        _broker = broker;
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Stay alive when the window is closed; the tray icon is the real lifetime.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _vm = new HubViewModel(_broker);
            _window = new HubWindow(_vm);
            _window.Closing += (_, e) =>
            {
                // Hide instead of close so the daemon keeps running in the tray.
                e.Cancel = true;
                _window!.Hide();
            };

            BuildTray(desktop);

            // Start TRAY-ONLY: the hub is a background daemon, so it must not pop a window on
            // launch. The window is created but stays hidden until the user opens it from the
            // tray (we deliberately do NOT set desktop.MainWindow, which would auto-show it;
            // ShutdownMode.OnExplicitShutdown keeps the app alive with no window open).
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                _vm?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void BuildTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Open hub window");
        open.Click += (_, _) => ShowWindow();

        var quit = new NativeMenuItem("Quit hub");
        quit.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(BuildClaudeSetupMenu());
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        _tray = new TrayIcon
        {
            ToolTipText = $"Keincheck Hub {HubWindow.VersionLabel()}",
            Icon = LoadTrayIcon(),
            Menu = menu,
        };
        _tray.Clicked += (_, _) => ShowWindow();

        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();

        // The first time the user opens the window, offer to wire Keincheck into Claude
        // (once, marker-gated). Deferred to here — never on launch — so the hub stays
        // tray-only until intentionally opened.
        Dispatcher.UIThread.Post(TryOfferFirstRunSetup, DispatcherPriority.Background);
    }

    /// <summary>The "Set up in Claude ▸ …" tray submenu — re-runnable, registers the MCP server.</summary>
    private NativeMenuItem BuildClaudeSetupMenu()
    {
        var submenu = new NativeMenu();

        var code = new NativeMenuItem("Claude Code");
        code.Click += (_, _) => SetupClaude(ClaudeTarget.Code);

        var desktopItem = new NativeMenuItem("Claude Desktop");
        desktopItem.Click += (_, _) => SetupClaude(ClaudeTarget.Desktop);

        var both = new NativeMenuItem("Both");
        both.Click += (_, _) => SetupClaude(ClaudeTarget.Code, ClaudeTarget.Desktop);

        submenu.Add(code);
        submenu.Add(desktopItem);
        submenu.Add(both);

        return new NativeMenuItem("Set up in Claude") { Menu = submenu };
    }

    private void SetupClaude(params ClaudeTarget[] targets)
    {
        var connectExe = ClaudeMcpSetup.ResolveConnectExe();
        if (connectExe is null)
        {
            ClaudeSetupUi.ShowNote("Keincheck",
                "Could not locate keincheck-connect.exe. Set the KEINCHECK_CONNECT_EXE environment " +
                "variable to its full path, or reinstall the hub so the bridge is co-located.");
            return;
        }
        ClaudeSetupUi.RunAndShowResult(targets, connectExe);
    }

    /// <summary>
    /// On first run, offer to register Keincheck in Claude — but only once (a marker file), and
    /// only when it is not already configured anywhere. Best-effort; never throws into startup.
    /// </summary>
    private static void TryOfferFirstRunSetup()
    {
        try
        {
            var marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Keincheck", "claude-setup.offered");
            if (File.Exists(marker))
                return;

            var connectExe = ClaudeMcpSetup.ResolveConnectExe();
            if (connectExe is null)
                return; // nothing to point Claude at

            void MarkOffered()
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                    File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("o"));
                }
                catch { /* best-effort */ }
            }

            if (ClaudeMcpSetup.IsConfigured(ClaudeTarget.Code, connectExe)
                || ClaudeMcpSetup.IsConfigured(ClaudeTarget.Desktop, connectExe))
            {
                MarkOffered(); // already set up somewhere — don't nag
                return;
            }

            ClaudeSetupUi.ShowFirstRunOffer(connectExe, MarkOffered);
        }
        catch
        {
            // First-run convenience only; a failure here must never break the daemon.
        }
    }

    private static WindowIcon? LoadTrayIcon()
    {
        // Prefer an embedded asset; fall back to null (Avalonia draws a default badge).
        try
        {
            var uri = new Uri("avares://Keincheck.Hub/Assets/tray.ico");
            if (AssetLoader.Exists(uri))
                using (var stream = AssetLoader.Open(uri))
                    return new WindowIcon(stream);
        }
        catch
        {
            // No asset available — run without a custom tray glyph.
        }
        return null;
    }
}
