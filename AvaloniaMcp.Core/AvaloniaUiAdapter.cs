using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AvaloniaMcp.Core;

/// <summary>
/// The Avalonia 12 implementation of <see cref="IUiAdapter"/>. It owns the
/// concrete toolkit calls — root enumeration, tree walks, the
/// <see cref="AvaloniaPropertyRegistry"/>, <see cref="RenderTargetBitmap"/>
/// rendering, UI-Automation peers, synthetic routed input, hit-testing, focus,
/// and the <see cref="BindingErrorSink"/> — so the MCP tools (Phase B) can route
/// every primitive through this single seam.
/// </summary>
/// <remarks>
/// Construct it with the shared <see cref="PropertyValueSerializer"/> (and an
/// optional <see cref="BindingErrorSink"/>) the host already registers as DI
/// singletons. All members are UI-thread-affine exactly like the tool bodies they
/// back; the adapter does not re-marshal.
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
    public IEnumerable<Visual> EnumerateRoots() => ControlRegistry.EnumerateRoots();

    /// <inheritdoc />
    public TopLevel? GetTopLevel(Visual visual) => TopLevel.GetTopLevel(visual);

    /// <inheritdoc />
    public IEnumerable<Control> GetLogicalChildren(Control control)
    {
        foreach (var lc in ((ILogical)control).LogicalChildren)
            if (lc is Control c)
                yield return c;
    }

    /// <inheritdoc />
    public IEnumerable<Control> GetVisualChildren(Control control)
    {
        foreach (var vc in control.GetVisualChildren())
            if (vc is Control c)
                yield return c;
    }

    // ---------------------------------------------------------------- metadata

    /// <inheritdoc />
    public string GetTypeName(Control control) => control.GetType().Name;

    /// <inheritdoc />
    public string? GetName(Control control) => string.IsNullOrEmpty(control.Name) ? null : control.Name;

    /// <inheritdoc />
    public string? GetTitle(Control control) => control is Window w ? w.Title : null;

    /// <inheritdoc />
    public Rect GetBounds(Control control) => control.Bounds;

    /// <inheritdoc />
    public bool IsEffectivelyVisible(Control control) => control.IsEffectivelyVisible;

    /// <inheritdoc />
    public bool IsEffectivelyEnabled(Control control) => control.IsEffectivelyEnabled;

    /// <inheritdoc />
    public bool IsActiveWindow(Control control) => control is WindowBase wb && wb.IsActive;

    // -------------------------------------------------------------- properties

    /// <inheritdoc />
    public IEnumerable<AvaloniaProperty> GetRegisteredProperties(Control control) =>
        AvaloniaPropertyRegistry.Instance.GetRegistered(control);

    /// <inheritdoc />
    public object? ReadProperty(Control control, AvaloniaProperty property) =>
        _serializer.Read(control, property);

    /// <inheritdoc />
    public object? ReadProperty(Control control, string propertyName) =>
        _serializer.Read(control, propertyName);

    /// <inheritdoc />
    public bool WriteProperty(Control control, string propertyName, JsonElement value, out string error) =>
        _serializer.TryWrite(control, propertyName, value, out error);

    // ----------------------------------------------------------------- render

    /// <inheritdoc />
    public bool TryRenderControlToPng(Control control, int maxDimension, out byte[] png, out string error)
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

    /// <inheritdoc />
    public bool TryRenderVisualToPng(Visual visual, int maxDimension, Rect? cropRect, out byte[] png, out string error)
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
    public UiAutomationResult InvokeAutomation(Control control, UiAutomationAction action, string? value)
    {
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
            var expand = ec.ExpandCollapseState != Avalonia.Automation.ExpandCollapseState.Expanded;
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
    public bool SetFocus(Control control) => control.Focus();

    /// <inheritdoc />
    public Control? GetFocusedElement(TopLevel topLevel) =>
        topLevel.FocusManager?.GetFocusedElement() as Control;

    // --------------------------------------------------------------- hit-test

    /// <inheritdoc />
    public Control? HitTest(TopLevel topLevel, Point point) =>
        topLevel.InputHitTest(point) as Control;

    // ----------------------------------------------------- synthetic input

    /// <inheritdoc />
    public Control? SendPointer(TopLevel topLevel, PointerAction action, Point point)
    {
        var target = SyntheticInput.HitTest(topLevel, point);
        if (target is null)
            return null;

        var button = action == PointerAction.RightClick ? MouseButton.Right : MouseButton.Left;

        switch (action)
        {
            case PointerAction.Move:
                SyntheticInput.RaiseMove(topLevel, target, point);
                break;
            case PointerAction.Down:
                SyntheticInput.RaisePressed(topLevel, target, point, button, clickCount: 1);
                break;
            case PointerAction.Up:
                SyntheticInput.RaiseReleased(topLevel, target, point, button);
                break;
            case PointerAction.Click:
            case PointerAction.RightClick:
                SyntheticInput.RaisePressed(topLevel, target, point, button, clickCount: 1);
                SyntheticInput.RaiseReleased(topLevel, target, point, button);
                break;
            case PointerAction.DoubleClick:
                SyntheticInput.RaisePressed(topLevel, target, point, button, clickCount: 1);
                SyntheticInput.RaiseReleased(topLevel, target, point, button);
                SyntheticInput.RaisePressed(topLevel, target, point, button, clickCount: 2);
                SyntheticInput.RaiseReleased(topLevel, target, point, button);
                break;
        }

        return target;
    }

    /// <inheritdoc />
    public Control? SendWheel(TopLevel topLevel, Point point, Vector delta)
    {
        var target = SyntheticInput.HitTest(topLevel, point);
        if (target is null)
            return null;
        SyntheticInput.RaiseWheel(topLevel, target, point, delta);
        return target;
    }

    /// <inheritdoc />
    public Control? SendText(Control? target, string text)
    {
        (target as InputElement)?.Focus(NavigationMethod.Unspecified, KeyModifiers.None);

        var sink = ResolveInputSink(target);
        if (sink is null)
            return null;

        SyntheticInput.RaiseText(sink, text);
        return sink as Control;
    }

    /// <inheritdoc />
    public bool SendKeys(
        Control? target, string chords,
        out IReadOnlyList<string> sentChords, out Control? sink, out string error)
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

        var element = ResolveInputSink(target);
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

        foreach (var root in ControlRegistry.EnumerateRoots().OfType<TopLevel>())
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
