using System.Windows;
using Keincheck.Client;
using Keincheck.Core;

namespace Keincheck.Wpf;

/// <summary>
/// WPF integration for the thin broker client. Call <see cref="UseKeincheckClient"/>
/// from your WPF <c>App</c> startup to connect the app to the Keincheck hub over the
/// named pipe. Mirrors the Avalonia <c>UseMcpClient</c> surface, wiring the WPF
/// <see cref="WpfUiAdapter"/> + <see cref="WpfUiDispatcher"/> into the framework-free
/// <see cref="BrokerClientHost"/>.
/// </summary>
public static class ApplicationClientExtensions
{
    /// <summary>
    /// Starts the broker client for <paramref name="app"/> and returns a handle that
    /// disconnects gracefully when disposed.
    /// </summary>
    public static IDisposable UseKeincheckClient(this Application app, Action<McpClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new McpClientOptions();
        configure(options);

        var adapter = new WpfUiAdapter();
        var dispatcher = new WpfUiDispatcher();

        var client = BrokerClientHost.Start(adapter, dispatcher, options);

        // Tie graceful teardown to app exit.
        app.Exit += (_, _) => client.Dispose();
        return client;
    }
}
