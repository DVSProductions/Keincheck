using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace AvaloniaMcp.Client;

/// <summary>
/// <see cref="AppBuilder"/> integration for the thin broker client. Call
/// <see cref="UseMcpClient"/> in <c>Program.cs</c> to connect the app to the hub.
/// Mirrors the embedded <c>UseMcpServer</c> surface but starts a pipe client instead
/// of an in-process Kestrel host.
/// </summary>
public static class AppBuilderClientExtensions
{
    /// <summary>
    /// Connects the application produced by <paramref name="builder"/> to the
    /// AvaloniaMcp hub over the named pipe. The client starts after framework setup
    /// (so the spine/UI thread exist) and disconnects gracefully on shutdown.
    /// </summary>
    public static AppBuilder UseMcpClient(this AppBuilder builder, Action<McpClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new McpClientOptions();
        configure?.Invoke(options);

        return builder.AfterSetup(b =>
        {
            var app = b.Instance;
            if (app is null)
                return;

            BrokerClient? client = BrokerClient.Start(app, options);

            void WireShutdown()
            {
                if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Exit += (_, _) =>
                    {
                        // Fire-and-forget graceful teardown; bounded so exit isn't blocked.
                        var c = client;
                        client = null;
                        if (c is not null)
                            c.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                    };
                }
            }

            if (app.ApplicationLifetime is not null)
                WireShutdown();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(WireShutdown);
        });
    }
}
