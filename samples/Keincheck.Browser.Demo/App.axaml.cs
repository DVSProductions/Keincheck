using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Keincheck.Browser.Demo;

/// <summary>
/// The browser app runs under <see cref="ISingleViewApplicationLifetime"/> — there are no
/// windows, just one root view. <c>AvaloniaUiAdapter</c> already handles that case: it takes
/// <c>MainView</c> and walks up to its <c>TopLevel</c>, which is what every UI tool operates on.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// What this build was enrolled against, or null when it was not enrolled. Shown in the UI
    /// so the page itself says whether it can reach a hub, rather than leaving you guessing.
    /// </summary>
    public static string? AttachTarget { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = new MainView();

        base.OnFrameworkInitializationCompleted();
    }
}
