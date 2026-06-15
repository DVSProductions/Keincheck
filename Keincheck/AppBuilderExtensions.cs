using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Keincheck.Core;

namespace Keincheck;

/// <summary>
/// <see cref="AppBuilder"/> integration. Call <see cref="UseMcpServer"/> in your
/// <c>Program.cs</c> while configuring the Avalonia app builder to attach the
/// embedded MCP server.
/// </summary>
public static class AppBuilderExtensions
{
    /// <summary>
    /// Attaches the embedded MCP server to the application produced by
    /// <paramref name="builder"/>. The server starts once the Avalonia
    /// framework has finished initializing (so a <c>TopLevel</c>/lifetime
    /// exists) and stops when the application's main lifetime ends.
    /// </summary>
    /// <param name="builder">The Avalonia app builder.</param>
    /// <param name="configure">Optional callback to tweak <see cref="McpServerOptions"/>.</param>
    public static AppBuilder UseMcpServer(this AppBuilder builder, Action<McpServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new McpServerOptions();
        configure?.Invoke(options);

        return builder.AfterSetup(b =>
        {
            var app = b.Instance;
            if (app is null)
                return;

            // Start the background MCP host now. AfterSetup runs on the UI thread
            // after Initialize(); the host itself only touches the visual tree
            // lazily (inside tool handlers, via UiDispatch), so the lifetime need
            // not exist yet.
            IDisposable? host = McpHost.Attach(app, options);

            // Tie shutdown to the desktop lifetime so the background host thread
            // is torn down cleanly on exit. The lifetime may not be assigned yet
            // when AfterSetup runs, so post the wiring back onto the dispatcher,
            // by which point StartWith...Lifetime has set ApplicationLifetime.
            void WireShutdown()
            {
                if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Exit += (_, _) =>
                    {
                        host?.Dispose();
                        host = null;
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
