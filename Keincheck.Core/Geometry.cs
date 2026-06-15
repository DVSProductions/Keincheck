namespace Keincheck.Core;

/// <summary>
/// A framework-neutral axis-aligned rectangle in device-independent pixels. Mirrors
/// the shape of Avalonia's <c>Rect</c> / WPF's <c>Rect</c> without depending on
/// either toolkit. The adapter converts the framework's own rectangle type to/from
/// this neutral form at the seam.
/// </summary>
public readonly record struct UiRect(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge (<see cref="X"/> + <see cref="Width"/>).</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge (<see cref="Y"/> + <see cref="Height"/>).</summary>
    public double Bottom => Y + Height;

    /// <summary>An empty rectangle at the origin.</summary>
    public static UiRect Empty => default;
}

/// <summary>
/// A framework-neutral 2-D point in device-independent pixels. Mirrors the shape of
/// Avalonia's <c>Point</c> / WPF's <c>Point</c>.
/// </summary>
public readonly record struct UiPoint(double X, double Y);

/// <summary>
/// A framework-neutral 2-D vector (used for pointer-wheel deltas). Mirrors the shape
/// of Avalonia's <c>Vector</c> / WPF's <c>Vector</c>.
/// </summary>
public readonly record struct UiVector(double X, double Y);

/// <summary>
/// One numbered annotation in a "set-of-marks" screenshot: the <paramref name="Number"/>
/// drawn on the image and the <paramref name="Rect"/> (in the rendered top-level's client
/// DIP coordinates) the numbered box outlines. Built by <c>screenshot_marked</c> and
/// handed to <see cref="IUiAdapter.TryRenderAnnotated"/> for the adapter to draw.
/// </summary>
public readonly record struct UiMark(int Number, UiRect Rect);

/// <summary>
/// A framework-neutral accessibility view of an element, mirroring what a screen-reader
/// (or a UI-Automation peer) would expose: its <paramref name="Role"/> (e.g.
/// <c>"Button"</c>), accessible <paramref name="Name"/>, current <paramref name="Value"/>,
/// whether it is <paramref name="IsInteractive"/> (clickable / editable / togglable), and
/// the active <paramref name="States"/> (e.g. <c>"checked"</c>, <c>"disabled"</c>,
/// <c>"focused"</c>). Produced by <see cref="IUiAdapter.GetSemanticInfo"/> and surfaced by
/// the semantic tools so the model reasons over roles and states rather than raw types.
/// </summary>
public readonly record struct UiSemanticInfo(
    string Role,
    string? Name,
    string? Value,
    bool IsInteractive,
    IReadOnlyList<string> States);
