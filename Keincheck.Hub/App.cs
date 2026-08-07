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
    private readonly HubSettings _settings;
    private HubViewModel? _vm;
    private HubWindow? _window;
    private TrayIcon? _tray;

    public App(PipeClientBroker broker, HubSettings? settings = null)
    {
        _broker = broker;
        _settings = settings ?? HubSettings.Open();
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

            _vm = new HubViewModel(_broker, HubRuntime.Mcp);
            _window = new HubWindow(_vm);
            _window.Closing += (_, e) =>
            {
                // With a tray to fall back to, hide instead of close so the daemon keeps
                // running. Without one the window is the ONLY handle on this process, so
                // closing it has to mean "quit" — otherwise the user is left with a hub they
                // can neither see nor stop. See HubSettings.StartInTrayOnly.
                if (!_settings.StartInTrayOnly)
                {
                    desktop.Shutdown();
                    return;
                }
                e.Cancel = true;
                _window!.Hide();
            };

            BuildTray(desktop);

            // Tray-only where a tray is guaranteed (Windows, macOS): the hub is a background
            // daemon and must not pop a window on launch. The window is created but stays
            // hidden until the user opens it from the tray (we deliberately do NOT set
            // desktop.MainWindow, which would auto-show it; ShutdownMode.OnExplicitShutdown
            // keeps the app alive with no window open).
            //
            // On Linux there is no guaranteed tray — a stock GNOME session has no
            // StatusNotifierItem host — so the window is shown instead, and the user opts in
            // to tray-only from the tray menu once they can see their tray icon works.
            if (!_settings.StartInTrayOnly)
                Dispatcher.UIThread.Post(ShowWindow, DispatcherPriority.Background);

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
        menu.Add(BuildAgentSetupMenu());
        menu.Add(BuildStartAtLoginItem());
        menu.Add(BuildStartInTrayItem());

        // Only offered when this hub was built with remote support. Opening the panel does
        // not enable anything; it is where an operator turns the listener on and issues the
        // credentials that clients on other machines need.
        if (HubRuntime.Remote is { } remote)
        {
            var remoteItem = new NativeMenuItem("Remote access…");
            remoteItem.Click += (_, _) => ShowRemoteWindow(remote);
            menu.Add(remoteItem);
        }

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

        // The first time the user opens the window, offer to wire Keincheck into their AI
        // client (once, marker-gated). Deferred to here — never on launch — so the hub
        // stays tray-only until intentionally opened.
        Dispatcher.UIThread.Post(TryOfferFirstRunSetup, DispatcherPriority.Background);
    }

    private Window? _remoteWindow;

    /// <summary>Opens (or re-focuses) the remote-access panel.</summary>
    private void ShowRemoteWindow(Remote.RemoteAccess remote)
    {
        // Rebuilt each time it is opened so the credential list and listener state are fresh;
        // an operator who just revoked something must not be shown a stale view of it.
        if (_remoteWindow is not null)
        {
            try { _remoteWindow.Close(); } catch { /* already gone */ }
            _remoteWindow = null;
        }

        _remoteWindow = HubWindow.BuildRemoteWindow(remote);
        _remoteWindow.Closed += (_, _) => _remoteWindow = null;
        _remoteWindow.Show();
        _remoteWindow.Activate();
    }

    /// <summary>The "Set up AI assistant ▸ …" tray submenu — re-runnable, registers the MCP server.</summary>
    private NativeMenuItem BuildAgentSetupMenu()
    {
        var submenu = new NativeMenu();

        var code = new NativeMenuItem("Claude Code");
        code.Click += (_, _) => SetupAgents(AgentTarget.ClaudeCode);

        var desktopItem = new NativeMenuItem("Claude Desktop");
        desktopItem.Click += (_, _) => SetupAgents(AgentTarget.ClaudeDesktop);

        var kimi = new NativeMenuItem("Kimi Code");
        kimi.Click += (_, _) => SetupAgents(AgentTarget.KimiCode);

        var all = new NativeMenuItem("All");
        all.Click += (_, _) => SetupAgents(AgentTarget.ClaudeCode, AgentTarget.ClaudeDesktop, AgentTarget.KimiCode);

        submenu.Add(code);
        submenu.Add(desktopItem);
        submenu.Add(kimi);
        submenu.Add(all);

        return new NativeMenuItem("Set up AI assistant") { Menu = submenu };
    }

    /// <summary>
    /// The login-startup toggle. Opting out only skips the registration — the
    /// keincheck-connect shim still launches the hub on demand when an AI connects.
    /// </summary>
    private NativeMenuItem BuildStartAtLoginItem()
    {
        var item = new NativeMenuItem("Start hub at login")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.StartAtLogin,
        };
        item.Click += (_, _) =>
        {
            // Settings are the source of truth (some platforms auto-toggle IsChecked before
            // Click, some don't), then the checkbox is forced back to it.
            var enable = !_settings.StartAtLogin;
            _settings.SetStartAtLogin(enable);
            if (enable)
                StartupRegistration.TryRegister();
            else
                StartupRegistration.TryUnregister();
            item.IsChecked = enable;
        };
        return item;
    }

    /// <summary>
    /// Toggles whether the hub launches with no window (a pure tray daemon). Reachable from
    /// the tray, so a user only ever sees it once they have proved their tray works — which
    /// is exactly the precondition for turning it on. Takes effect on the next launch.
    /// </summary>
    private NativeMenuItem BuildStartInTrayItem()
    {
        var item = new NativeMenuItem("Start hidden in tray")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.StartInTrayOnly,
        };
        item.Click += (_, _) =>
        {
            var enable = !_settings.StartInTrayOnly;
            _settings.SetStartInTrayOnly(enable);
            item.IsChecked = enable;
        };
        return item;
    }

    private void SetupAgents(params AgentTarget[] targets)
    {
        var connectExe = AgentMcpSetup.ResolveConnectExe();
        if (connectExe is null)
        {
            AgentSetupUi.ShowNote("Keincheck",
                "Could not locate the keincheck-connect bridge. Set the KEINCHECK_CONNECT_EXE " +
                "environment variable to its full path, or reinstall the hub so the bridge is " +
                "co-located.");
            return;
        }
        AgentSetupUi.RunAndShowResult(targets, connectExe);
    }

    /// <summary>
    /// On first run, offer to register Keincheck in the user's AI clients — but only once (a
    /// marker file), and only when it is not already configured anywhere. Best-effort; never
    /// throws into startup.
    /// </summary>
    private static void TryOfferFirstRunSetup()
    {
        try
        {
            // Legacy marker name from when only Claude was supported; renaming it would
            // re-show the offer to every existing user, so it stays.
            var marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Keincheck", "claude-setup.offered");
            if (File.Exists(marker))
                return;

            var connectExe = AgentMcpSetup.ResolveConnectExe();
            if (connectExe is null)
                return; // nothing to point the clients at

            void MarkOffered()
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                    File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("o"));
                }
                catch { /* best-effort */ }
            }

            if (AgentMcpSetup.IsConfigured(AgentTarget.ClaudeCode, connectExe)
                || AgentMcpSetup.IsConfigured(AgentTarget.ClaudeDesktop, connectExe)
                || AgentMcpSetup.IsConfigured(AgentTarget.KimiCode, connectExe))
            {
                MarkOffered(); // already set up somewhere — don't nag
                return;
            }

            AgentSetupUi.ShowFirstRunOffer(connectExe, MarkOffered);
        }
        catch
        {
            // First-run convenience only; a failure here must never break the daemon.
        }
    }

    private static WindowIcon? LoadTrayIcon()
    {
        // .ico is the Windows shell's format; the DBus StatusNotifierItem (Linux) and
        // NSStatusItem (macOS) hosts both want a plain bitmap, so PNG leads off-Windows.
        // Either candidate is tried in turn, and a total miss falls back to null (Avalonia
        // draws a default badge) rather than leaving the hub with no tray icon at all.
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "tray.ico", "tray.png" }
            : new[] { "tray.png", "tray.ico" };

        foreach (var name in candidates)
        {
            try
            {
                var uri = new Uri($"avares://Keincheck.Hub/Assets/{name}");
                if (!AssetLoader.Exists(uri))
                    continue;
                using var stream = AssetLoader.Open(uri);
                return new WindowIcon(stream);
            }
            catch
            {
                // Unreadable or undecodable on this platform — try the next candidate.
            }
        }
        return null;
    }
}
