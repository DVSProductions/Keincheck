using Avalonia;
using Avalonia.Browser;
using Keincheck.Avalonia;
using Keincheck.Client;

namespace Keincheck.Browser.Demo;

/// <summary>
/// Browser entry point. The only Keincheck-specific part is the connector: a browser has no
/// named pipes, so the default transport cannot reach the hub and the WebSocket one is used
/// instead. Everything else about wiring a client is identical to the desktop sample.
/// </summary>
internal static class Program
{
    private static Task Main(string[] args) => BuildAvaloniaApp()
        .StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();

        // FromCredential() reads the credential the build embedded, so this app attaches to the
        // hub it was compiled against and no other. Null means the build was not enrolled --
        // no hub installed, or the endpoint was never enabled.
        var connector = WebSocketChannelConnector.FromCredential();

        // UseMcpClient is applied ONLY when there is a connector, and that is not a nicety.
        // BrokerClient falls back to the named-pipe connector when McpClientOptions.Connector is
        // null, and a browser has no named pipes -- so an unenrolled build would not degrade
        // quietly, it would throw on the first connect attempt.
        if (connector is not null)
        {
            App.AttachTarget = connector.Describe();
            builder = builder.UseMcpClient(o =>
            {
                o.AppId = "browserdemo";
                o.Connector = connector;
            });
        }
        else
        {
            App.AttachTarget = null;
        }

        return builder;
    }
}
