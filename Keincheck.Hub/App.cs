using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;

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

            desktop.MainWindow = _window;
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
        menu.Add(quit);

        _tray = new TrayIcon
        {
            ToolTipText = "Keincheck Hub",
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
