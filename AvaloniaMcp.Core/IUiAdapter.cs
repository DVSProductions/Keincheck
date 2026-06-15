using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace AvaloniaMcp.Core;

/// <summary>
/// The framework-agnostic seam between the MCP tool set and the live UI toolkit.
/// Every primitive the 22 tools need — enumerating top-levels, walking the
/// logical/visual tree, reading/writing properties, rendering to PNG,
/// UI-Automation invocation, synthetic pointer/key input, hit-testing, focus, and
/// recent binding errors — is expressed here so the tools never call the toolkit
/// directly. <see cref="AvaloniaUiAdapter"/> implements this over Avalonia 12; a
/// future broker/headless backend can supply a different implementation.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Unless explicitly noted, every member touches the live
/// visual tree and therefore MUST be called on the UI thread. Callers marshal via
/// <see cref="UiDispatch"/> exactly as the tools do today; the adapter itself does
/// not re-marshal.</para>
/// <para><b>Types.</b> Members are typed to Avalonia's <see cref="Control"/>,
/// <see cref="Visual"/>, <see cref="TopLevel"/>, and <see cref="Point"/> because
/// Core already references Avalonia. The abstraction is over the <i>operations</i>,
/// not the element types — this is the single place Phase B reroutes tool bodies
/// through, and the surface is complete enough that no tool needs a primitive
/// outside it.</para>
/// </remarks>
public interface IUiAdapter
{
    // ---------------------------------------------------------------- topology

    /// <summary>
    /// All open top-level visuals of the current application (desktop windows,
    /// popups, or the single-view root). UI-thread only.
    /// </summary>
    IEnumerable<Visual> EnumerateRoots();

    /// <summary>
    /// The <see cref="TopLevel"/> that hosts <paramref name="visual"/>, or
    /// <c>null</c> if it is not attached to one. (Wraps
    /// <c>TopLevel.GetTopLevel</c>.)
    /// </summary>
    TopLevel? GetTopLevel(Visual visual);

    /// <summary>
    /// The direct <b>logical</b> children of <paramref name="control"/> that are
    /// themselves controls, in document order.
    /// </summary>
    IEnumerable<Control> GetLogicalChildren(Control control);

    /// <summary>
    /// The direct <b>visual</b> children of <paramref name="control"/> that are
    /// themselves controls (includes template-generated parts), in document order.
    /// </summary>
    IEnumerable<Control> GetVisualChildren(Control control);

    // ---------------------------------------------------------------- metadata

    /// <summary>The runtime type name of <paramref name="control"/> (e.g. "Button").</summary>
    string GetTypeName(Control control);

    /// <summary>The control's <c>Name</c>, or <c>null</c> when unset/empty.</summary>
    string? GetName(Control control);

    /// <summary>The window/popup title if <paramref name="control"/> is a window, else <c>null</c>.</summary>
    string? GetTitle(Control control);

    /// <summary>The control's arranged bounds in its parent's coordinate space.</summary>
    Rect GetBounds(Control control);

    /// <summary>Whether the control is effectively visible (and all ancestors are).</summary>
    bool IsEffectivelyVisible(Control control);

    /// <summary>Whether the control is effectively enabled (and all ancestors are).</summary>
    bool IsEffectivelyEnabled(Control control);

    /// <summary>
    /// Whether <paramref name="control"/> is an active window
    /// (<c>WindowBase.IsActive</c>); <c>false</c> for non-window controls.
    /// </summary>
    bool IsActiveWindow(Control control);

    // -------------------------------------------------------------- properties

    /// <summary>
    /// Every Avalonia styled/attached property registered on
    /// <paramref name="control"/> (wraps
    /// <c>AvaloniaPropertyRegistry.Instance.GetRegistered</c>).
    /// </summary>
    IEnumerable<AvaloniaProperty> GetRegisteredProperties(Control control);

    /// <summary>
    /// Reads a styled/attached property as a JSON-friendly value (delegates to
    /// <see cref="PropertyValueSerializer"/>).
    /// </summary>
    object? ReadProperty(Control control, AvaloniaProperty property);

    /// <summary>
    /// Reads a CLR/styled property by name as a JSON-friendly value; <c>null</c>
    /// when missing or unreadable.
    /// </summary>
    object? ReadProperty(Control control, string propertyName);

    /// <summary>
    /// Coerces and writes <paramref name="value"/> onto the named property.
    /// Returns <c>false</c> with a reason in <paramref name="error"/> on failure;
    /// never throws for ordinary failures.
    /// </summary>
    bool WriteProperty(Control control, string propertyName, JsonElement value, out string error);

    // ----------------------------------------------------------------- render

    /// <summary>
    /// Renders <paramref name="control"/> (and its descendants) to PNG bytes,
    /// honoring <paramref name="maxDimension"/> (uniform downscale when larger).
    /// Returns <c>false</c> with a reason in <paramref name="error"/> when the
    /// control has no renderable size or rendering fails. UI-thread only.
    /// </summary>
    bool TryRenderControlToPng(Control control, int maxDimension, out byte[] png, out string error);

    /// <summary>
    /// Renders a <paramref name="visual"/> (typically a <see cref="TopLevel"/>) to
    /// PNG bytes, optionally cropping to <paramref name="cropRect"/> (in the
    /// visual's coordinate space). Returns <c>false</c> with a reason in
    /// <paramref name="error"/> on failure. UI-thread only.
    /// </summary>
    bool TryRenderVisualToPng(Visual visual, int maxDimension, Rect? cropRect, out byte[] png, out string error);

    // ------------------------------------------------------------- automation

    /// <summary>
    /// Performs a semantic UI-Automation action on <paramref name="control"/>.
    /// <paramref name="action"/> selects the pattern (<see cref="UiAutomationAction.Auto"/>
    /// auto-detects). <paramref name="value"/> is used by <c>SetValue</c> (and
    /// <c>Auto</c> on a value-capable control). Returns a structured outcome.
    /// </summary>
    UiAutomationResult InvokeAutomation(Control control, UiAutomationAction action, string? value);

    // ----------------------------------------------------------------- focus

    /// <summary>
    /// Requests keyboard focus for <paramref name="control"/>. Returns whether the
    /// focus request was accepted (callers may also re-check
    /// <c>control.IsFocused</c>).
    /// </summary>
    bool SetFocus(Control control);

    /// <summary>
    /// The control currently holding keyboard focus in <paramref name="topLevel"/>'s
    /// focus scope, or <c>null</c> if none / not a control.
    /// </summary>
    Control? GetFocusedElement(TopLevel topLevel);

    // --------------------------------------------------------------- hit-test

    /// <summary>
    /// The deepest control at <paramref name="point"/> (in
    /// <paramref name="topLevel"/> client coordinates), or <c>null</c> if nothing
    /// (or a non-control) is hit.
    /// </summary>
    Control? HitTest(TopLevel topLevel, Point point);

    // ----------------------------------------------------- synthetic input

    /// <summary>
    /// Raises a synthetic pointer gesture (<see cref="PointerAction.Move"/> /
    /// <c>Down</c> / <c>Up</c> / <c>Click</c> / <c>DoubleClick</c> / <c>RightClick</c>)
    /// at <paramref name="point"/> in <paramref name="topLevel"/>, hit-testing the
    /// target element. Returns the hit control, or <c>null</c> if nothing was hit.
    /// UI-thread only.
    /// </summary>
    Control? SendPointer(TopLevel topLevel, PointerAction action, Point point);

    /// <summary>
    /// Raises a synthetic mouse-wheel scroll (notches in <paramref name="delta"/>)
    /// at <paramref name="point"/>. Returns the hit control, or <c>null</c>.
    /// UI-thread only.
    /// </summary>
    Control? SendWheel(TopLevel topLevel, Point point, Vector delta);

    /// <summary>
    /// Routes literal <paramref name="text"/> as text-input to
    /// <paramref name="target"/> if given (focusing it first), else to the current
    /// focused element. Returns the control that received the text, or <c>null</c>
    /// if there was no sink. UI-thread only.
    /// </summary>
    Control? SendText(Control? target, string text);

    /// <summary>
    /// Sends one or more key chords (e.g. <c>"Ctrl+S"</c>, <c>"Enter"</c>) to
    /// <paramref name="target"/> if given (focusing it first), else to the focused
    /// element. <paramref name="chords"/> are whitespace/comma-separated. On a
    /// parse failure returns <c>false</c> with a reason in
    /// <paramref name="error"/>; on success <paramref name="sentChords"/> lists the
    /// accepted chords and <paramref name="sink"/> is the control that received
    /// them. UI-thread only.
    /// </summary>
    bool SendKeys(Control? target, string chords, out IReadOnlyList<string> sentChords, out Control? sink, out string error);

    // ------------------------------------------------------- diagnostics

    /// <summary>
    /// Up to <paramref name="count"/> most-recent captured Avalonia binding errors
    /// (oldest first); <paramref name="count"/> &lt;= 0 returns all buffered.
    /// Returns an empty sequence (and <paramref name="enabled"/> = false) when
    /// binding-error capture is disabled. Thread-safe.
    /// </summary>
    IReadOnlyList<string> GetRecentBindingErrors(int count, out bool enabled);
}

/// <summary>The semantic UI-Automation action requested via <see cref="IUiAdapter.InvokeAutomation"/>.</summary>
public enum UiAutomationAction
{
    /// <summary>Auto-detect the right pattern from the control's peer.</summary>
    Auto = 0,
    /// <summary>Invoke (push) the control.</summary>
    Invoke,
    /// <summary>Toggle the control.</summary>
    Toggle,
    /// <summary>Set the control's text/value (requires a value).</summary>
    SetValue,
    /// <summary>Expand the control.</summary>
    Expand,
    /// <summary>Collapse the control.</summary>
    Collapse,
    /// <summary>Select the item.</summary>
    Select,
}

/// <summary>The synthetic pointer gesture requested via <see cref="IUiAdapter.SendPointer"/>.</summary>
public enum PointerAction
{
    /// <summary>Move the pointer (no buttons).</summary>
    Move = 0,
    /// <summary>Left button down.</summary>
    Down,
    /// <summary>Left button up.</summary>
    Up,
    /// <summary>Left click (down + up).</summary>
    Click,
    /// <summary>Left double-click.</summary>
    DoubleClick,
    /// <summary>Right click (down + up).</summary>
    RightClick,
}

/// <summary>
/// The structured outcome of <see cref="IUiAdapter.InvokeAutomation"/>: whether it
/// succeeded, the pattern that was used, an optional human-readable state string,
/// and an error message on failure.
/// </summary>
public readonly record struct UiAutomationResult(
    bool Ok,
    string? Action,
    string? State,
    string? Error)
{
    /// <summary>A successful result for <paramref name="action"/> with optional <paramref name="state"/>.</summary>
    public static UiAutomationResult Success(string action, string? state = null) =>
        new(true, action, state, null);

    /// <summary>A failed result carrying <paramref name="error"/>.</summary>
    public static UiAutomationResult Failure(string error) =>
        new(false, null, null, error);
}
