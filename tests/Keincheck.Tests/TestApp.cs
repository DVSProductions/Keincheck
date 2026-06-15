using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Keincheck.Tests;

// Tells Avalonia.Headless which type exposes the BuildAvaloniaApp entry point.
// HeadlessUnitTestSession.GetOrStartForAssembly(...) reads this attribute to spin
// up a single headless Application for the whole test assembly.
[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Keincheck.Tests;

/// <summary>
/// Minimal Avalonia <see cref="Application"/> used by the headless test session.
/// It is intentionally code-only (no XAML and no theme package) so the test
/// project needs no extra AvaloniaResource wiring or theme reference beyond the
/// frozen package set. The spine tests exercise the logical/visual tree, the
/// styled-property system, and the dispatcher — none of which require a control
/// theme — so a bare <see cref="Application"/> is sufficient and keeps the
/// harness self-contained against only the referenced packages.
/// </summary>
public sealed class TestApp : Application
{
    /// <summary>
    /// Headless app builder entry point. Discovered via
    /// <see cref="AvaloniaTestApplicationAttribute"/> and invoked by
    /// <see cref="HeadlessUnitTestSession.GetOrStartForAssembly(System.Reflection.Assembly)"/>.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // No real GPU/skia surface needed for these spine tests; the
                // logical/visual tree, property system, and dispatcher are enough.
                UseHeadlessDrawing = true,
            });
}
