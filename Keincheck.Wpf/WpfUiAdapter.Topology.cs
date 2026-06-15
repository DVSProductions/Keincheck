using System.Windows;
using System.Windows.Media;
using Keincheck.Core;

namespace Keincheck.Wpf;

/// <summary>
/// Topology + metadata group of <see cref="WpfUiAdapter"/>: root enumeration, top-level
/// resolution, logical/visual child walks, the type/name/title predicates, arranged
/// bounds, and the effective visible/enabled/active-window flags.
/// <para>
/// <b>Stage B map (System.Windows.*):</b>
/// <list type="bullet">
///   <item><see cref="EnumerateRoots"/> → <c>Application.Current.Windows</c>.</item>
///   <item><see cref="GetTopLevel"/> → <c>Window.GetWindow(dependencyObject)</c>.</item>
///   <item><see cref="GetLogicalChildren"/> → <c>LogicalTreeHelper.GetChildren</c> (all children; consumers filter via <see cref="IsControl"/>).</item>
///   <item><see cref="GetVisualChildren"/> → <c>VisualTreeHelper.GetChildrenCount</c>/<c>GetChild</c> loop.</item>
///   <item><see cref="IsControl"/> → <c>element is FrameworkElement</c> (or <c>Control</c>).</item>
///   <item><see cref="MatchesType"/> → walk <c>GetType().BaseType</c> by simple name (ordinal).</item>
///   <item><see cref="GetName"/> → <c>(FrameworkElement).Name</c>; <see cref="GetTitle"/> → <c>(Window).Title</c>.</item>
///   <item><see cref="GetBounds"/> → <c>VisualTreeHelper.GetOffset(visual)</c> + <c>RenderSize</c>, mapped to <see cref="UiRect"/>.</item>
///   <item><see cref="IsEffectivelyVisible"/> → <c>(UIElement).IsVisible</c>; <see cref="IsEffectivelyEnabled"/> → <c>(UIElement).IsEnabled</c>; <see cref="IsActiveWindow"/> → <c>(Window).IsActive</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class WpfUiAdapter
{
    // ---------------------------------------------------------------- topology

    /// <inheritdoc />
    public IEnumerable<object> EnumerateRoots() => EnumerateRootsCore();

    /// <summary>
    /// Every open top-level window of the current application. WPF has no single-view
    /// lifetime (the Avalonia <c>ISingleViewApplicationLifetime</c> branch has no analog),
    /// so the application's <see cref="Window"/> collection is the complete set of roots.
    /// Safe to call only on the UI thread.
    /// </summary>
    internal static IEnumerable<Visual> EnumerateRootsCore()
    {
        var app = Application.Current;
        if (app is null)
            yield break;

        // Application.Windows is a live collection mutated on the UI thread; snapshot it
        // into a local array first so a window opening/closing mid-walk cannot throw an
        // "Collection was modified" from this lazy iterator.
        var windows = app.Windows;
        var snapshot = new Window[windows.Count];
        windows.CopyTo(snapshot, 0);

        foreach (var w in snapshot)
            if (w is not null)
                yield return w;
    }

    /// <inheritdoc />
    public object? GetTopLevel(object element) =>
        element is DependencyObject d ? Window.GetWindow(d) : null;

    /// <inheritdoc />
    public IEnumerable<object> GetLogicalChildren(object element)
    {
        if (element is not DependencyObject d)
            yield break;
        // Yield every logical child element (not just Controls): the selector walk needs
        // to traverse THROUGH non-Control visuals (template internals, content presenters)
        // to reach controls beneath them. Consumers that want controls only filter via
        // IsControl. LogicalTreeHelper can return raw CLR objects (strings, view-models);
        // only Visual-derived children are addressable handles, mirroring the Avalonia
        // adapter's "if (lc is Visual v)" gate.
        foreach (var child in LogicalTreeHelper.GetChildren(d))
            if (child is Visual v)
                yield return v;
    }

    /// <inheritdoc />
    public IEnumerable<object> GetVisualChildren(object element)
    {
        // VisualTreeHelper only accepts a Visual/Visual3D; a logical-only node (e.g. a
        // FlowDocument run reached via the logical tree) has no visual children here.
        if (element is not Visual visual)
            yield break;
        var count = VisualTreeHelper.GetChildrenCount(visual);
        for (var i = 0; i < count; i++)
            if (VisualTreeHelper.GetChild(visual, i) is Visual vc)
                yield return vc;
    }

    // ---------------------------------------------------------------- metadata

    /// <inheritdoc />
    /// <remarks>
    /// WPF's <c>System.Windows.Controls.Control</c> excludes <c>TextBlock</c>, the
    /// <c>Panel</c> family, and other primitives that ARE addressable controls in the
    /// Avalonia tree (where they derive from <c>Avalonia.Controls.Control</c>). To match
    /// the Avalonia adapter's tool outputs (tree dumps, selector matches, <c>get_text</c>),
    /// the WPF "control" gate is <see cref="FrameworkElement"/> — the closest analog that
    /// also covers <c>TextBlock</c>/<c>Panel</c>/custom <c>FrameworkElement</c> subclasses.
    /// </remarks>
    public bool IsControl(object element) => element is FrameworkElement;

    /// <inheritdoc />
    public string GetTypeName(object element) => element.GetType().Name;

    /// <inheritdoc />
    public bool MatchesType(object element, string typeName)
    {
        for (var t = element.GetType(); t is not null && t != typeof(object); t = t.BaseType)
        {
            if (string.Equals(t.Name, typeName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <inheritdoc />
    public string? GetName(object element) =>
        element is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) ? fe.Name : null;

    /// <inheritdoc />
    public string? GetTitle(object element) => element is Window w ? w.Title : null;

    /// <inheritdoc />
    public UiRect GetBounds(object element)
    {
        // The neutral contract (and the Avalonia reference, which returns Control.Bounds)
        // reports arranged bounds in the element's PARENT coordinate space. The WPF
        // equivalent of Avalonia's parent-relative Control.Bounds is the visual's arrange
        // offset relative to its visual parent (VisualTreeHelper.GetOffset) plus its
        // RenderSize. A top-level Window has a (0,0) offset, matching Avalonia where a
        // top-level's Bounds origin is the origin.
        if (element is FrameworkElement fe)
        {
            var size = fe.RenderSize;
            // GetOffset throws for a Visual not yet connected to a visual tree; treat an
            // unparented element as origin-relative rather than failing the metadata read.
            Vector offset;
            try
            {
                offset = VisualTreeHelper.GetOffset(fe);
            }
            catch
            {
                offset = default;
            }
            return new UiRect(offset.X, offset.Y, size.Width, size.Height);
        }
        return UiRect.Empty;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Maps the element's own (0,0)→RenderSize box through
    /// <c>TransformToAncestor(topLevel)</c>, yielding the element's rendered bounds in the
    /// top-level's client DIP space — the box set-of-marks overlays are drawn at. WPF's
    /// <see cref="Visual.TransformToAncestor(Visual)"/> throws an
    /// <see cref="InvalidOperationException"/> when the two visuals do not share a common
    /// visual root (the contract's "not in the same tree" case), which we catch and report
    /// as <c>false</c> with the parent-relative <see cref="GetBounds"/> fallback. The
    /// transform composes the offset of every intermediate visual, so a translated/scaled
    /// ancestor is accounted for exactly.
    /// </remarks>
    public bool TryGetBoundsInTopLevel(object element, object topLevel, out UiRect rect)
    {
        // Default (and failure) value is the parent-relative bounds, matching the seam's
        // documented "no usable on-screen box" fallback semantics.
        rect = GetBounds(element);

        if (element is not Visual visual || topLevel is not Visual root)
            return false;

        // A visual cannot be its own meaningful ancestor here, and TransformToAncestor of a
        // visual to itself is identity — treat the top-level as already top-level-relative.
        try
        {
            var transform = visual.TransformToAncestor(root);
            var local = visual is FrameworkElement fe ? new Rect(fe.RenderSize) : new Rect(default(Size));
            var mapped = transform.TransformBounds(local);
            rect = new UiRect(mapped.X, mapped.Y, mapped.Width, mapped.Height);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Not in the same visual tree (no common ancestor) — keep the parent-relative
            // fallback already in `rect` and report no usable on-screen box.
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsEffectivelyVisible(object element) => element is UIElement u && u.IsVisible;

    /// <inheritdoc />
    public bool IsEffectivelyEnabled(object element) => element is UIElement u && u.IsEnabled;

    /// <inheritdoc />
    public bool IsActiveWindow(object element) => element is Window w && w.IsActive;
}
