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

        // Credential issuance is a one-shot command, not a hub run. It is handled BEFORE the
        // single-instance election so it works while a hub is already up -- which is the
        // normal case on a developer machine, and the case a build step hits.
        if (args.Contains(Remote.CredentialCli.Verb, StringComparer.Ordinal))
            return Remote.CredentialCli.Run(args);

        // Single-instance election: hold the per-user mutex for the hub's lifetime.
        using var mutex = new Mutex(initiallyOwned: true, PipeNames.SingleInstanceMutex, out var isFirst);
        if (!isFirst)
        {
            Console.Error.WriteLine("Keincheck hub is already running for this user.");
            return 0;
        }

        // Best-effort: register to start at login so the hub is up when the AI connects —
        // unless the user opted out, in which case remove any registration an older version
        // left behind. Opting out breaks nothing: keincheck-connect launches the hub on
        // demand when an AI client connects.
        var settings = HubSettings.Open();
        if (settings.StartAtLogin)
            StartupRegistration.TryRegister();
        else
            StartupRegistration.TryUnregister();

        var hubOptions = new HubOptions
        {
            // Advertise the real build on the MCP initialize handshake (not a hardcoded stub).
            ServerVersion = HubMetaTools.ResolveHubAssemblyVersion() ?? "0.0.0",
            // Static tooling mode (--static-tools or KEINCHECK_STATIC_TOOLS=1): the MCP
            // tool list never changes — agents that cannot handle dynamic tool additions
            // discover/call client tools via hub_list_client_tools / hub_call_tool instead.
            DynamicTooling = !StaticToolingRequested(args),
        };
        var brokerOptions = new BrokerOptions
        {
            PipeName = PipeNames.ControlPipe,
            InvokeTimeout = hubOptions.InvokeTimeout,
        };

        var broker = new PipeClientBroker(brokerOptions);
        broker.Start();

        // Remote access. Constructing this opens nothing and provisions nothing: with no
        // certificate authority there is nothing for a peer to authenticate against, so the
        // listener refuses to bind and the credential issuer refuses every request. A hub
        // whose owner never enabled remote access therefore holds no key material at all.
        var remote = new Keincheck.Hub.Remote.RemoteAccess(
            broker, broker.Audit, log: msg => Console.Error.WriteLine($"[keincheck-hub:remote] {msg}"));
        remote.StartIfEnabled();
        HubRuntime.Remote = remote;

        // The MCP server (meta-tools + proxy) + the MCP-over-pipe listener wrap the broker.
        HubRuntime.Start(broker, hubOptions);

        // Auto-update an installed hub in the background, applying only while idle. A no-op
        // for dev builds (CreateGithub returns null when this is not a Velopack install).
        HubUpdater? updater = null;
        if (hubOptions.AutoUpdate)
        {
            updater = HubUpdater.CreateGithub(
                broker, hubOptions.UpdateRepoUrl, hubOptions.UpdateCheckInterval,
                msg => Console.Error.WriteLine($"[keincheck-hub:update] {msg}"),
                agentSessionCount: () => HubRuntime.Mcp?.ConnectedSessionCount ?? 0);
            if (updater is not null && HubRuntime.Mcp is { } mcp)
                mcp.SessionsChanged += updater.PokeIdle;
            updater?.Start();
        }

        try
        {
            return BuildAvaloniaApp(broker, settings).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (updater is not null)
                updater.DisposeAsync().AsTask().GetAwaiter().GetResult();
            HubRuntime.StopAsync().GetAwaiter().GetResult();
            remote.DisposeAsync().AsTask().GetAwaiter().GetResult();
            broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>True when static tooling mode was requested via CLI flag or environment.</summary>
    private static bool StaticToolingRequested(string[] args)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, "--static-tools", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--no-dynamic-tools", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        var env = Environment.GetEnvironmentVariable("KEINCHECK_STATIC_TOOLS");
        return env is "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds the Avalonia app, injecting the live broker into the tray UI.</summary>
    public static AppBuilder BuildAvaloniaApp(PipeClientBroker broker, HubSettings? settings = null)
        => AppBuilder.Configure(() => new App(broker, settings))
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>Parameterless overload for the Avalonia previewer / design tooling.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure(() => new App(new PipeClientBroker()))
            .UsePlatformDetect()
            .LogToTrace();
}
