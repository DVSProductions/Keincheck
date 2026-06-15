using Avalonia;
using Keincheck.Client;

namespace Keincheck.Demo;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            // Attach the thin broker client (no embedded ASP.NET): connect to the hub
            // over the named pipe, register as "demo", and serve Core tool invocations
            // on the UI thread. Start the hub (or the keincheck-connect shim) to drive
            // this app end to end.
            .UseMcpClient(o => o.AppId = "demo");
}
