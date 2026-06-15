using Avalonia;
using Keincheck.Protocol;
using Velopack;

namespace Keincheck.Hub;

/// <summary>
/// Entry point for the Keincheck hub daemon. Single-instance per user: if a hub is
/// already running it exits immediately. Otherwise it runs the Velopack bootstrap,
/// registers for login-startup (best-effort), starts the live pipe-server broker + the
/// MCP servers (HTTP loopback + MCP-over-pipe), and shows the Avalonia tray UI.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack must run before anything else so install/update/uninstall hooks fire
        // and the app exits cleanly during those transient runs.
        VelopackApp.Build().Run();

        // Single-instance election: hold the per-user mutex for the hub's lifetime.
        using var mutex = new Mutex(initiallyOwned: true, PipeNames.SingleInstanceMutex, out var isFirst);
        if (!isFirst)
        {
            Console.Error.WriteLine("Keincheck hub is already running for this user.");
            return 0;
        }

        // Best-effort: register to start at login so the hub is up when the AI connects.
        StartupRegistration.TryRegister();

        var hubOptions = new HubOptions();
        var brokerOptions = new BrokerOptions
        {
            PipeName = PipeNames.ControlPipe,
            InvokeTimeout = hubOptions.InvokeTimeout,
        };

        var broker = new PipeClientBroker(brokerOptions);
        broker.Start();

        // The MCP server (meta-tools + proxy) + the MCP-over-pipe listener wrap the broker.
        HubRuntime.Start(broker, hubOptions);

        try
        {
            return BuildAvaloniaApp(broker).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            HubRuntime.StopAsync().GetAwaiter().GetResult();
            broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Builds the Avalonia app, injecting the live broker into the tray UI.</summary>
    public static AppBuilder BuildAvaloniaApp(PipeClientBroker broker)
        => AppBuilder.Configure(() => new App(broker))
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>Parameterless overload for the Avalonia previewer / design tooling.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure(() => new App(new PipeClientBroker()))
            .UsePlatformDetect()
            .LogToTrace();
}
