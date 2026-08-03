using Avalonia;
using Keincheck.Avalonia;

namespace ConsumerApp;

/// <summary>
/// The whole point of this fixture: one line of Keincheck wiring, reached through a
/// NuGet package reference rather than a project reference.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseMcpClient(o =>
            {
                o.AppId = "consumer";
                o.Log = message => Console.Error.WriteLine($"[keincheck] {message}");
            });
}
