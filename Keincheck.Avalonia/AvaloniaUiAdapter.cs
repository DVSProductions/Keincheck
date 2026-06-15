using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Keincheck.Core;

namespace Keincheck.Avalonia;

/// <summary>
/// The Avalonia 12 implementation of the framework-neutral <see cref="IUiAdapter"/>.
/// It owns the concrete toolkit calls — root enumeration, tree walks, the
/// <see cref="AvaloniaPropertyRegistry"/>, <see cref="RenderTargetBitmap"/> rendering,
/// UI-Automation peers, synthetic routed input, hit-testing, focus, and the
/// <see cref="BindingErrorSink"/> — and converts between Avalonia's element/geometry
/// types and the neutral <see cref="object"/> handles + <see cref="UiRect"/>/
/// <see cref="UiPoint"/>/<see cref="UiVector"/> structs at the seam.
/// </summary>
/// <remarks>
/// Construct it with the shared <see cref="PropertyValueSerializer"/> (and an optional
/// <see cref="BindingErrorSink"/>) the host registers as DI singletons. All members
/// are UI-thread-affine exactly like the tool bodies they back; the adapter does not
/// re-marshal.
/// </remarks>
public sealed class AvaloniaUiAdapter : IUiAdapter
{
    private readonly PropertyValueSerializer _serializer;
    private readonly BindingErrorSink? _bindingErrors;
    private readonly int _defaultMaxDimension;

    /// <param name="serializer">Shared property serializer (host DI singleton).</param>
    /// <param name="bindingErrors">
    /// Optional binding-error ring buffer. When null, <see cref="GetRecentBindingErrors"/>
    /// falls back to <see cref="BindingErrorSink.Current"/> if one is installed.
    /// </param>
    /// <param name="defaultMaxScreenshotDimension">
    /// Fallback max PNG dimension used only if a caller passes a non-positive value.
    /// </param>
    public AvaloniaUiAdapter(
        PropertyValueSerializer serializer,
        BindingErrorSink? bindingErrors = null,
        int defaultMaxScreenshotDimension = 2048)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _bindingErrors = bindingErrors;
        _defaultMaxDimension = defaultMaxScreenshotDimension > 0 ? defaultMaxScreenshotDimension : 2048;
    }

    // ---------------------------------------------------------------- topology

    /// <inheritdoc />
    public IEnumerable<object> EnumerateRoots() => EnumerateRootsCore();

    /// <summary>
    /// All open top-level visuals (windows, popups) of the current application. Safe to
    /// call only on the UI thread. (Moved here from the framework-free ControlRegistry.)
    /// </summary>
    internal static IEnumerable<Visual> EnumerateRootsCore()
    {
        var app = Application.Current;
        if (app is null)
            yield break;

        switch (app.ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                foreach (var w in desktop.Windows)
                    yield return w;
                break;
            case ISingleViewApplicationLifetime single when single.MainView is { } mv:
                if (TopLevel.GetTopLevel(mv) is { } tl)
                    yield return tl;
                else
                    yield return mv;
                break;
        }
    }

    /// <inheritdoc />
    public object? GetTopLevel(object element) =>
        element is Visual v ? TopLevel.GetTopLevel(v) : null;

    /// <inheritdoc />
    public IEnumerable<object> GetLogicalChildren(object element)
    {
        if (element is not ILogical logical)
            yield break;
        // Yield every logical child element (not just Controls): the selector walk needs
        // to traverse THROUGH non-Control visuals (template internals, adorner relays) to
        // reach controls beneath them. Consumers that want controls only filter via
        // IsControl. Only Visual-derived children are addressable handles.
        foreach (var lc in logical.LogicalChildren)
            if (lc is Visual v)
                yield return v;
    }

    /// <inheritdoc />
    public IEnumerable<object> GetVisualChildren(object element)
    {
        if (element is not Visual visual)
            yield break;
        foreach (var vc in visual.GetVisualChildren())
            yield return vc;
    }

    // ---------------------------------------------------------------- metadata

    /// <inheritdoc />
    public bool IsControl(object element) => element is Control;

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
        element is Control c && !string.IsNullOrEmpty(c.Name) ? c.Name : null;

    /// <inheritdoc />
    public IEnumerable<string> GetClasses(object element)
    {
        // StyledElement.Classes enumerates BOTH author style classes and framework
        // pseudo-classes (e.g. ":pointerover", ":pressed"), with pseudo-classes carrying a
        // leading ':'. The selector grammar's .class targets author classes only, so we skip
        // the colon-prefixed pseudo entries.
        if (element is not StyledElement se)
            return Array.Empty<string>();
        return se.Classes.Where(c => c.Length == 0 || c[0] != ':');
    }

    /// <inheritdoc />
    public string? GetTitle(object element) => element is Window w ? w.Title : null;

    /// <inheritdoc />
    public UiRect GetBounds(object element)
    {
        if (element is Control c)
        {
            var b = c.Bounds;
            return new UiRect(b.X, b.Y, b.Width, b.Height);
        }
        return UiRect.Empty;
    }

    /// <inheritdoc />
    public bool TryGetBoundsInTopLevel(object element, object topLevel, out UiRect rect)
    {
        // Default to the parent-relative box so a false return still carries SOMETHING
        // usable, exactly as the seam's default contract promises.
        rect = GetBounds(element);

        if (element is not Visual visual || topLevel is not Visual root)
            return false;

        // The element box is its own local size at the origin; mapping that AABB through
        // the element->top-level transform yields its rendered box in top-level client
        // DIPs (the coordinate space TryRenderAnnotated draws marks in). A null transform
        // means the two visuals are not connected through a shared coordinate root.
        if (visual.TransformToVisual(root) is not { } toRoot)
            return false;

        var mapped = new Rect(visual.Bounds.Size).TransformToAABB(toRoot);
        rect = new UiRect(mapped.X, mapped.Y, mapped.Width, mapped.Height);
        return true;
    }

    /// <inheritdoc />
    public UiSemanticInfo GetSemanticInfo(object element)
    {
        // No peer (non-control, or a control whose framework type has no peer): fall back
        // to the neutral type-name role + Name, as the seam default does.
        if (element is not Control control ||
            ControlAutomationPeer.CreatePeerForElement(control) is not { } peer)
        {
            return new UiSemanticInfo(GetTypeName(element), GetName(element), null, false, Array.Empty<string>());
        }

        // Role: the automation control type if the peer reports one, else the type name.
        var controlType = SafePeer(() => peer.GetAutomationControlType(), AutomationControlType.None);
        var role = controlType == AutomationControlType.None ? control.GetType().Name : controlType.ToString();

        // Name: the accessible name (peer) first, then the control's x:Name.
        var peerName = SafePeer(() => peer.GetName(), string.Empty);
        var name = !string.IsNullOrEmpty(peerName) ? peerName : GetName(element);

        // Value + states are gathered from whichever automation patterns the peer exposes.
        string? value = null;
        var states = new List<string>(4);

        if (peer.GetProvider<IValueProvider>() is { } valueProvider)
            value = valueProvider.Value;
        else if (peer.GetProvider<IRangeValueProvider>() is { } range)
            value = range.Value.ToString(CultureInfo.InvariantCulture);

        if (peer.GetProvider<IToggleProvider>() is { } toggle)
        {
            // Off/On/Indeterminate -> a "checked"/"unchecked"/"indeterminate" state.
            states.Add(toggle.ToggleState switch
            {
                ToggleState.On => "checked",
                ToggleState.Indeterminate => "indeterminate",
                _ => "unchecked",
            });
        }

        if (peer.GetProvider<ISelectionItemProvider>() is { } selection && selection.IsSelected)
            states.Add("selected");

        if (peer.GetProvider<IExpandCollapseProvider>() is { } expandCollapse)
        {
            states.Add(expandCollapse.ExpandCollapseState == global::Avalonia.Automation.ExpandCollapseState.Expanded
                ? "expanded"
                : "collapsed");
        }

        if (!control.IsEffectivelyEnabled)
            states.Add("disabled");
        if (SafePeer(() => peer.HasKeyboardFocus(), false))
            states.Add("focused");

        var interactive = IsInteractivePeer(control, peer);
        return new UiSemanticInfo(role, name, value, interactive, states);
    }

    /// <summary>
    /// Whether the element is interactive: it can take keyboard focus, or its automation
    /// peer exposes an actionable pattern (invoke/toggle/value/expand-collapse/selection),
    /// or its runtime type is a well-known interactive control.
    /// </summary>
    private static bool IsInteractivePeer(Control control, AutomationPeer peer)
    {
        if (SafePeer(() => peer.IsKeyboardFocusable(), false))
            return true;

        if (peer.GetProvider<IInvokeProvider>() is not null ||
            peer.GetProvider<IToggleProvider>() is not null ||
            peer.GetProvider<IExpandCollapseProvider>() is not null ||
            peer.GetProvider<ISelectionItemProvider>() is not null ||
            peer.GetProvider<IValueProvider>() is { IsReadOnly: false } ||
            peer.GetProvider<IRangeValueProvider>() is { IsReadOnly: false })
            return true;

        // Type fallback for controls whose peer pattern set is empty but which the user
        // still operates (e.g. a Hyperlink/TabItem/ListBoxItem template part). Walk the
        // base chain by simple name, mirroring MatchesType.
        for (var t = control.GetType(); t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var typeName in InteractiveTypeNames)
                if (string.Equals(t.Name, typeName, StringComparison.Ordinal))
                    return true;
        }

        return false;
    }

    // Simple type names (matched up the base chain) treated as interactive when no
    // actionable automation pattern is detected.
    private static readonly string[] InteractiveTypeNames =
    {
        "Button", "ToggleButton", "RepeatButton", "MenuItem", "TabItem", "ListBoxItem",
        "TreeViewItem", "ComboBox", "TextBox", "Slider", "CheckBox", "RadioButton",
        "ToggleSwitch", "HyperlinkButton", "SplitButton", "NumericUpDown", "DatePicker",
        "TimePicker", "CalendarDatePicker", "AutoCompleteBox", "ScrollBar", "Thumb",
    };

    /// <summary>
    /// Runs an automation-peer accessor that some peers implement by throwing
    /// (<c>NotSupportedException</c> / <c>NotImplementedException</c>) and returns
    /// <paramref name="fallback"/> instead of letting it bubble out of a read-only query.
    /// </summary>
    private static T SafePeer<T>(Func<T> get, T fallback)
    {
        try { return get(); }
        catch { return fallback; }
    }

    /// <inheritdoc />
    public bool IsEffectivelyVisible(object element) => element is Control c && c.IsEffectivelyVisible;

    /// <inheritdoc />
    public bool IsEffectivelyEnabled(object element) => element is Control c && c.IsEffectivelyEnabled;

    /// <inheritdoc />
    public bool IsActiveWindow(object element) => element is WindowBase wb && wb.IsActive;

    // -------------------------------------------------------------- properties

    /// <inheritdoc />
    public IEnumerable<string> GetPropertyNames(object element)
    {
        if (element is not Control control)
            yield break;
        foreach (var prop in AvaloniaPropertyRegistry.Instance.GetRegistered(control))
            yield return prop.Name;
    }

    /// <inheritdoc />
    public bool TryReadProperty(object element, string name, out object? jsonFriendlyValue)
    {
        jsonFriendlyValue = null;
        if (element is not Control control || string.IsNullOrEmpty(name))
            return false;

        // Prefer the styled/attached property of that name (more values are reachable
        // that way), then fall back to a CLR property read — mirroring the v1
        // serializer's two read paths so values are identical to before the refactor.
        var avProp = AvaloniaPropertyRegistry.Instance.GetRegistered(control)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (avProp is not null)
        {
            try
            {
                jsonFriendlyValue = Project(control.GetValue(avProp));
                return true;
            }
            catch
            {
                jsonFriendlyValue = null;
                return false;
            }
        }

        var clr = FindClrProperty(control.GetType(), name);
        if (clr is null || !clr.CanRead || clr.GetIndexParameters().Length > 0)
            return false;

        try
        {
            jsonFriendlyValue = Project(clr.GetValue(control));
            return true;
        }
        catch
        {
            jsonFriendlyValue = null;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryWriteProperty(object element, string name, JsonElement value, out string error)
    {
        if (element is not Control control)
        {
            error = "Target is not a control.";
            return false;
        }

        if (string.IsNullOrEmpty(name))
        {
            error = "Property name is required.";
            return false;
        }

        var prop = FindClrProperty(control.GetType(), name);
        if (prop is null)
        {
            error = $"Property '{name}' not found on {control.GetType().Name}.";
            return false;
        }

        if (!prop.CanWrite || prop.SetMethod is null || !prop.SetMethod.IsPublic)
        {
            error = $"Property '{name}' is not writable.";
            return false;
        }

        if (prop.GetIndexParameters().Length > 0)
        {
            error = $"Property '{name}' is an indexer and cannot be set.";
            return false;
        }

        if (!PropertyValueSerializer.TryCoerce(value, prop.PropertyType, out var coerced, out error))
            return false;

        try
        {
            prop.SetValue(control, coerced);
            error = string.Empty;
            return true;
        }
        catch (TargetInvocationException tie)
        {
            error = $"Setting '{name}' threw: {tie.InnerException?.Message ?? tie.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Setting '{name}' failed: {ex.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public object? GetDataContext(object element) => (element as StyledElement)?.DataContext;

    /// <summary>
    /// Projects an Avalonia framework value to a JSON-friendly form via the neutral
    /// serializer, supplying the Avalonia-specific element + value-type renderers.
    /// </summary>
    private object? Project(object? value) =>
        _serializer.ToJsonFriendly(value, RenderElement, RenderLeaf);

    private static string? RenderElement(object value) =>
        value is Control control
            ? $"{control.GetType().Name}{(string.IsNullOrEmpty(control.Name) ? "" : "#" + control.Name)}"
            : null;

    private static string? RenderLeaf(object value)
    {
        var type = value.GetType();
        // Known Avalonia structs serialize cleanly via their invariant string form.
        return type.Namespace?.StartsWith("Avalonia", StringComparison.Ordinal) == true && type.IsValueType
            ? value.ToString()
            : null;
    }

    private static PropertyInfo? FindClrProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    // ----------------------------------------------------------------- render

    /// <inheritdoc />
    public bool TryRenderToPng(object element, int maxDim, out byte[] png, out string error)
    {
        // A Window/TopLevel renders as a whole visual; any other control renders its
        // own subtree (with a cropped-TopLevel fallback). This unifies the v1
        // TryRenderControlToPng / TryRenderVisualToPng split behind one neutral method.
        if (element is TopLevel topLevel)
            return TryRenderVisualToPng(topLevel, maxDim, cropRect: null, out png, out error);
        if (element is Control control)
            return TryRenderControlToPng(control, maxDim, out png, out error);

        png = Array.Empty<byte>();
        error = "Target is not a renderable visual.";
        return false;
    }

    /// <inheritdoc />
    public bool TryRenderAnnotated(
        object topLevel, int maxDim, IReadOnlyList<UiMark> marks, out byte[] png, out string error)
    {
        png = Array.Empty<byte>();
        error = string.Empty;

        if (topLevel is not Visual visual)
        {
            error = "Annotated render target is not a renderable visual.";
            return false;
        }

        // Nothing to overlay: fall back to the plain render path so callers still get a
        // usable (un-annotated) screenshot honoring maxDim.
        if (marks is null || marks.Count == 0)
            return TryRenderToPng(topLevel, maxDim, out png, out error);

        var max = maxDim > 0 ? maxDim : _defaultMaxDimension;
        var fullSize = visual.Bounds.Size;
        if (fullSize.Width <= 0 || fullSize.Height <= 0)
        {
            error = $"The visual has no renderable size ({fullSize.Width}x{fullSize.Height}).";
            return false;
        }

        // Render the top-level at exactly the scale TryRenderToPng would use, so the
        // overlay lands on the same pixels and maxDim is honored identically.
        var (pixelW, pixelH, scale) = ClampToPixels(fullSize.Width, fullSize.Height, max);

        try
        {
            // The RenderTargetBitmap's 96*scale DPI means its drawing context works in the
            // SAME DIP units as the visual; the device scaling is applied by the bitmap.
            // So marks (already in top-level client DIPs) are drawn at their DIP rects with
            // no manual scaling — the bitmap maps DIP -> the downscaled pixel grid for us.
            using var rtb = new RenderTargetBitmap(
                new PixelSize(pixelW, pixelH), new Vector(96 * scale, 96 * scale));
            rtb.Render(visual);

            using (var ctx = rtb.CreateDrawingContext(clear: false))
            {
                DrawMarks(ctx, marks);
            }

            png = Encode(rtb);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Annotated render failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Draws each <see cref="UiMark"/> onto <paramref name="ctx"/> as a high-contrast 2px
    /// box (in top-level DIP coordinates) plus a small filled number badge at its
    /// top-left corner. Coordinates are DIPs; the target bitmap applies the render scale.
    /// </summary>
    private static void DrawMarks(DrawingContext ctx, IReadOnlyList<UiMark> marks)
    {
        // Magenta stroke over a thin black under-stroke = legible on both light and dark
        // UI. Yellow badge fill with black text reads against almost any backdrop.
        var outline = new Pen(new SolidColorBrush(Colors.Magenta), 2);
        var halo = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 4);
        var badgeFill = new SolidColorBrush(Colors.Yellow);
        var badgeStroke = new Pen(new SolidColorBrush(Colors.Black), 1);
        var textBrush = new SolidColorBrush(Colors.Black);
        var typeface = Typeface.Default;

        foreach (var mark in marks)
        {
            var r = mark.Rect;
            if (r.Width <= 0 || r.Height <= 0)
                continue;

            var box = new Rect(r.X, r.Y, r.Width, r.Height);

            // Dark halo first (slightly thicker) so the magenta box stays visible even on
            // a magenta-ish background, then the bright outline on top.
            ctx.DrawRectangle(null, halo, box);
            ctx.DrawRectangle(null, outline, box);

            // Number badge anchored at the box's top-left, nudged inside the border.
            var label = mark.Number.ToString(CultureInfo.InvariantCulture);
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                12,
                textBrush);

            const double padX = 3, padY = 1;
            var badgeW = text.Width + padX * 2;
            var badgeH = text.Height + padY * 2;
            var badge = new Rect(box.X, box.Y, badgeW, badgeH);

            ctx.DrawRectangle(badgeFill, badgeStroke, badge);
            ctx.DrawText(text, new Point(badge.X + padX, badge.Y + padY));
        }
    }

    private bool TryRenderControlToPng(Control control, int maxDimension, out byte[] png, out string error)
    {
        png = Array.Empty<byte>();
        error = string.Empty;
        var max = maxDimension > 0 ? maxDimension : _defaultMaxDimension;

        var localSize = control.Bounds.Size;
        if (localSize.Width <= 0 || localSize.Height <= 0)
        {
            error = $"Control '{Describe(control)}' has no renderable size " +
                    $"({localSize.Width}x{localSize.Height}); it may not be laid out or visible.";
            return false;
        }

        var (pixelW, pixelH, scale) = ClampToPixels(localSize.Width, localSize.Height, max);

        // Primary path: render the control subtree directly.
        try
        {
            using var rtb = new RenderTargetBitmap(
                new PixelSize(pixelW, pixelH), new Vector(96 * scale, 96 * scale));
            rtb.Render(control);
            png = Encode(rtb);
            return true;
        }
        catch (Exception ex)
        {
            // Fallback: render the whole TopLevel and crop to the control's bounds.
            var topLevel = TopLevel.GetTopLevel(control);
            if (topLevel is null)
            {
                error = $"Direct render of '{Describe(control)}' failed ({ex.Message}) and the control has no TopLevel to crop from.";
                return false;
            }

            if (control.TransformToVisual(topLevel) is not { } toRoot)
            {
                error = $"Direct render of '{Describe(control)}' failed and its bounds could not be mapped to the window.";
                return false;
            }

            var cropRect = new Rect(localSize).TransformToAABB(toRoot);
            return TryRenderVisualToPng(topLevel, max, cropRect, out png, out error);
        }
    }

    private bool TryRenderVisualToPng(Visual visual, int maxDimension, Rect? cropRect, out byte[] png, out string error)
    {
        png = Array.Empty<byte>();
        error = string.Empty;
        var max = maxDimension > 0 ? maxDimension : _defaultMaxDimension;

        var fullSize = visual.Bounds.Size;
        if (fullSize.Width <= 0 || fullSize.Height <= 0)
        {
            error = $"The visual has no renderable size ({fullSize.Width}x{fullSize.Height}).";
            return false;
        }

        var (pixelW, pixelH, scale) = ClampToPixels(fullSize.Width, fullSize.Height, max);

        using var rtb = new RenderTargetBitmap(
            new PixelSize(pixelW, pixelH), new Vector(96 * scale, 96 * scale));
        rtb.Render(visual);

        if (cropRect is not { } crop)
        {
            png = Encode(rtb);
            return true;
        }

        var clamped = crop.Intersect(new Rect(fullSize));
        if (clamped.Width <= 0 || clamped.Height <= 0)
        {
            error = "The crop region is empty after clamping to the visual.";
            return false;
        }

        var (cropW, cropH, cropScale) = ClampToPixels(clamped.Width, clamped.Height, max);
        using var cropped = new RenderTargetBitmap(
            new PixelSize(cropW, cropH), new Vector(96 * cropScale, 96 * cropScale));
        using (var ctx = cropped.CreateDrawingContext())
        {
            var dest = new Rect(0, 0, clamped.Width, clamped.Height);
            var src = new Rect(clamped.X, clamped.Y, clamped.Width, clamped.Height);
            ctx.DrawImage(rtb, src, dest);
        }

        png = Encode(cropped);
        return true;
    }

    private static byte[] Encode(RenderTargetBitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms); // PNG encode
        return ms.ToArray();
    }

    private static (int width, int height, double scale) ClampToPixels(
        double dipWidth, double dipHeight, int max)
    {
        var scale = 1.0;
        var largest = Math.Max(dipWidth, dipHeight);
        if (largest > max)
            scale = max / largest;

        var w = Math.Max(1, (int)Math.Round(dipWidth * scale));
        var h = Math.Max(1, (int)Math.Round(dipHeight * scale));
        return (w, h, scale);
    }

    // ------------------------------------------------------------- automation

    /// <inheritdoc />
    public UiAutomationResult InvokeAutomation(object element, UiAutomationAction action, string? value)
    {
        if (element is not Control control)
            return UiAutomationResult.Failure("Target is not a control.");

        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        if (peer is null)
            return UiAutomationResult.Failure("No automation peer is available for this control.");

        try
        {
            return action switch
            {
                UiAutomationAction.Invoke   => DoInvoke(peer),
                UiAutomationAction.Toggle   => DoToggle(peer),
                UiAutomationAction.SetValue => DoSetValue(peer, value),
                UiAutomationAction.Expand   => DoExpand(peer, expand: true),
                UiAutomationAction.Collapse => DoExpand(peer, expand: false),
                UiAutomationAction.Select   => DoSelect(peer),
                _                           => DoAuto(peer, value),
            };
        }
        catch (Exception ex)
        {
            return UiAutomationResult.Failure($"Automation action failed: {ex.Message}");
        }
    }

    private static UiAutomationResult DoAuto(AutomationPeer peer, string? value)
    {
        if (value is not null && peer.GetProvider<IValueProvider>() is { IsReadOnly: false })
            return DoSetValue(peer, value);

        if (peer.GetProvider<IInvokeProvider>() is { } invoke)
        {
            invoke.Invoke();
            return UiAutomationResult.Success("Invoke");
        }

        if (peer.GetProvider<IToggleProvider>() is { } toggle)
        {
            toggle.Toggle();
            return UiAutomationResult.Success("Toggle", toggle.ToggleState.ToString());
        }

        if (peer.GetProvider<IExpandCollapseProvider>() is { } ec)
        {
            var expand = ec.ExpandCollapseState != global::Avalonia.Automation.ExpandCollapseState.Expanded;
            if (expand) ec.Expand(); else ec.Collapse();
            return UiAutomationResult.Success(expand ? "Expand" : "Collapse", ec.ExpandCollapseState.ToString());
        }

        if (peer.GetProvider<ISelectionItemProvider>() is { } sel)
        {
            sel.Select();
            return UiAutomationResult.Success("Select", $"isSelected={sel.IsSelected}");
        }

        if (peer.GetProvider<IValueProvider>() is { IsReadOnly: false } valNoArg)
            return UiAutomationResult.Failure(
                $"Control only supports the Value pattern; provide a 'value' to set (current: {valNoArg.Value}).");

        return UiAutomationResult.Failure(
            "Control's automation peer exposes no actionable pattern (Invoke/Toggle/ExpandCollapse/SelectionItem/Value).");
    }

    private static UiAutomationResult DoInvoke(AutomationPeer peer)
    {
        if (peer.GetProvider<IInvokeProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the Invoke pattern.");
        p.Invoke();
        return UiAutomationResult.Success("Invoke");
    }

    private static UiAutomationResult DoToggle(AutomationPeer peer)
    {
        if (peer.GetProvider<IToggleProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the Toggle pattern.");
        p.Toggle();
        return UiAutomationResult.Success("Toggle", p.ToggleState.ToString());
    }

    private static UiAutomationResult DoSetValue(AutomationPeer peer, string? value)
    {
        if (value is null)
            return UiAutomationResult.Failure("A 'value' string is required for SetValue.");
        if (peer.GetProvider<IValueProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the Value pattern.");
        if (p.IsReadOnly)
            return UiAutomationResult.Failure("Control's Value pattern is read-only.");
        p.SetValue(value);
        return UiAutomationResult.Success("SetValue", p.Value);
    }

    private static UiAutomationResult DoExpand(AutomationPeer peer, bool expand)
    {
        if (peer.GetProvider<IExpandCollapseProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the ExpandCollapse pattern.");
        if (expand) p.Expand(); else p.Collapse();
        return UiAutomationResult.Success(expand ? "Expand" : "Collapse", p.ExpandCollapseState.ToString());
    }

    private static UiAutomationResult DoSelect(AutomationPeer peer)
    {
        if (peer.GetProvider<ISelectionItemProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the SelectionItem pattern.");
        p.Select();
        return UiAutomationResult.Success("Select", $"isSelected={p.IsSelected}");
    }

    // ----------------------------------------------------------------- focus

    /// <inheritdoc />
    public bool SetFocus(object element) => element is Control c && c.Focus();

    /// <inheritdoc />
    public object? GetFocusedElement(object topLevel) =>
        (topLevel as TopLevel)?.FocusManager?.GetFocusedElement() as Control;

    // --------------------------------------------------------------- hit-test

    /// <inheritdoc />
    public object? HitTest(object topLevel, UiPoint point) =>
        (topLevel as TopLevel)?.InputHitTest(new Point(point.X, point.Y)) as Control;

    // ----------------------------------------------------- synthetic input

    /// <inheritdoc />
    public object? SendPointer(object topLevel, PointerAction action, UiPoint point)
    {
        if (topLevel is not TopLevel tl)
            return null;

        var p = new Point(point.X, point.Y);
        var target = SyntheticInput.HitTest(tl, p);
        if (target is null)
            return null;

        var button = action == PointerAction.RightClick ? MouseButton.Right : MouseButton.Left;

        switch (action)
        {
            case PointerAction.Move:
                SyntheticInput.RaiseMove(tl, target, p);
                break;
            case PointerAction.Down:
                SyntheticInput.RaisePressed(tl, target, p, button, clickCount: 1);
                break;
            case PointerAction.Up:
                SyntheticInput.RaiseReleased(tl, target, p, button);
                break;
            case PointerAction.Click:
            case PointerAction.RightClick:
                SyntheticInput.RaisePressed(tl, target, p, button, clickCount: 1);
                SyntheticInput.RaiseReleased(tl, target, p, button);
                break;
            case PointerAction.DoubleClick:
                SyntheticInput.RaisePressed(tl, target, p, button, clickCount: 1);
                SyntheticInput.RaiseReleased(tl, target, p, button);
                SyntheticInput.RaisePressed(tl, target, p, button, clickCount: 2);
                SyntheticInput.RaiseReleased(tl, target, p, button);
                break;
        }

        return target;
    }

    /// <inheritdoc />
    public object? SendWheel(object topLevel, UiPoint point, UiVector delta)
    {
        if (topLevel is not TopLevel tl)
            return null;

        var p = new Point(point.X, point.Y);
        var target = SyntheticInput.HitTest(tl, p);
        if (target is null)
            return null;
        SyntheticInput.RaiseWheel(tl, target, p, new Vector(delta.X, delta.Y));
        return target;
    }

    /// <inheritdoc />
    public object? SendText(object? target, string text)
    {
        (target as InputElement)?.Focus(NavigationMethod.Unspecified, KeyModifiers.None);

        var sink = ResolveInputSink(target as Control);
        if (sink is null)
            return null;

        SyntheticInput.RaiseText(sink, text);
        return sink as Control;
    }

    /// <inheritdoc />
    public bool SendKeys(
        object? target, string chords,
        out IReadOnlyList<string> sentChords, out object? sink, out string error)
    {
        sentChords = Array.Empty<string>();
        sink = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(chords))
        {
            error = "keys was empty.";
            return false;
        }

        var parsed = new List<(Key key, KeyModifiers mods, string raw)>();
        foreach (var token in chords.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SyntheticInput.TryParseChord(token, out var key, out var mods, out var parseError))
            {
                error = $"could not parse key chord '{token}': {parseError}";
                return false;
            }
            parsed.Add((key, mods, token));
        }

        if (parsed.Count == 0)
        {
            error = "no key chords parsed from input.";
            return false;
        }

        (target as InputElement)?.Focus(NavigationMethod.Unspecified, KeyModifiers.None);

        var element = ResolveInputSink(target as Control);
        if (element is null)
        {
            error = "no focused element to receive keys (focus a control via handle/selector first).";
            return false;
        }

        var sent = new List<string>(parsed.Count);
        foreach (var (key, mods, raw) in parsed)
        {
            SyntheticInput.RaiseKey(element, key, mods, down: true);
            SyntheticInput.RaiseKey(element, key, mods, down: false);
            sent.Add(raw);
        }

        sentChords = sent;
        sink = element as Control;
        return true;
    }

    private static IInputElement? ResolveInputSink(Control? explicitTarget)
    {
        if (explicitTarget is IInputElement ie)
            return ie;

        foreach (var root in EnumerateRootsCore().OfType<TopLevel>())
        {
            var focused = root.FocusManager?.GetFocusedElement();
            if (focused is not null)
                return focused;
        }

        return null;
    }

    // ------------------------------------------------------- diagnostics

    /// <inheritdoc />
    public IReadOnlyList<string> GetRecentBindingErrors(int count, out bool enabled)
    {
        var sink = _bindingErrors ?? BindingErrorSink.Current;
        if (sink is null)
        {
            enabled = false;
            return Array.Empty<string>();
        }

        enabled = true;
        return sink.Recent(count).ToArray();
    }

    // --------------------------------------------------------------- helpers

    private static string Describe(Control control)
    {
        var name = control.Name;
        var typeName = control.GetType().Name;
        return string.IsNullOrEmpty(name) ? typeName : $"{typeName}#{name}";
    }
}
