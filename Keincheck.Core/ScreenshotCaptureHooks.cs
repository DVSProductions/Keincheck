namespace Keincheck.Core;

/// <summary>
/// Host-app extension point for screenshot rendering. Some app content never goes through the
/// framework's bitmap render path — e.g. Avalonia composition custom visuals (video or camera
/// feeds), which <c>RenderTargetBitmap</c> does not execute, so they capture as black. An app
/// can register a scope factory here; every Keincheck screenshot render enters one scope for
/// the duration of the render, giving the app a signal to draw such content into the capture
/// (typically via a <c>Render</c> override gated on the scope).
/// </summary>
public static class ScreenshotCaptureHooks
{
    /// <summary>
    /// Factory invoked on the UI thread before each screenshot render; the returned scope is
    /// disposed once the rendered bitmap has been encoded. Null (the default) means no hook.
    /// Factories should be reentrant — nested renders enter nested scopes.
    /// </summary>
    public static Func<IDisposable>? EnterScope { get; set; }

    /// <summary>
    /// Enters the registered scope. Returns null when no hook is registered; a throwing host
    /// hook is swallowed — it must never break screenshot capture itself.
    /// </summary>
    public static IDisposable? Enter()
    {
        try
        {
            return EnterScope?.Invoke();
        }
        catch
        {
            return null;
        }
    }
}
