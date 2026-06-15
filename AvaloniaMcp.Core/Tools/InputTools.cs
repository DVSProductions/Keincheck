using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using ModelContextProtocol.Server;

namespace AvaloniaMcp.Core.Tools;

/// <summary>
/// Synthetic-input fallback for custom-drawn controls that expose no UI Automation
/// peer. Where the automation-based tools cannot reach a control (e.g. a control
/// that draws itself and only handles raw pointer/key events), these tools drive
/// genuine Avalonia routed input events through <see cref="IUiAdapter"/>
/// (<see cref="IUiAdapter.SendPointer"/>, <see cref="IUiAdapter.SendWheel"/>,
/// <see cref="IUiAdapter.SendText"/>, <see cref="IUiAdapter.SendKeys"/>) so the tool
/// layer never fabricates events itself.
/// </summary>
/// <remarks>
/// <para>
/// Addressing model: pointer tools work in the coordinate space of a target
/// <see cref="TopLevel"/> (a window or single-view root). You may point at a
/// specific window by control handle/selector (its containing top level is used)
/// or, when omitted, the first open top level is targeted. The given
/// <c>x</c>/<c>y</c> are interpreted as positions within that top level; the actual
/// element under the cursor is found by the adapter and the event is raised on it so
/// it bubbles/tunnels normally.
/// </para>
/// <para>
/// Every method marshals all visual-tree access onto the UI thread via
/// <see cref="UiDispatch"/> and returns a structured, JSON-serializable result.
/// Bad handles/selectors/coordinates never throw — they return
/// <c>{ ok = false, error = "…" }</c>.
/// </para>
/// </remarks>
[McpServerToolType]
public static class InputTools
{
    /// <summary>
    /// Synthesizes a pointer action (move / down / up / click / double / right) at a
    /// point inside a top level, raising real Avalonia pointer events on the element
    /// hit-tested at that point.
    /// </summary>
    [McpServerTool, Description(
        "Synthetic pointer input at window coordinates for custom-drawn (peer-less) controls. " +
        "action = move|down|up|click|double|right. (x,y) are coordinates inside the target top level. " +
        "Optionally pass a control handle or selector to choose which window's top level to target; " +
        "otherwise the first open top level is used. Returns the handle + type of the element that was hit.")]
    public static async Task<object> Pointer(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Action: move, down, up, click (down+up), double (double-click), or right (right-click).")] string action,
        [Description("X coordinate within the target top level (device-independent pixels).")] double x,
        [Description("Y coordinate within the target top level (device-independent pixels).")] double y,
        [Description("Optional control handle (e.g. \"ctl-3\") whose window/top level should receive the input.")] string? handle = null,
        [Description("Optional CSS-ish selector used (first match) to pick the target window/top level when no handle is given.")] string? selector = null)
    {
        var act = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!TryMapPointerAction(act, out var pointerAction))
            return Fail($"unknown action '{action}'. expected move|down|up|click|double|right.");

        return await UiDispatch.Run(() =>
        {
            if (!TryResolveTopLevel(registry, ui, handle, selector, out var topLevel, out var error))
                return Fail(error);

            var target = ui.SendPointer(topLevel, pointerAction, new Point(x, y));
            if (target is null)
                return Fail($"no control hit-tested at ({x}, {y}) in the target top level.");

            var button = act == "right" ? "Right" : "Left";

            return Ok(new
            {
                action = act,
                x,
                y,
                button,
                target = registry.Assign(target),
                targetType = ui.GetTypeName(target),
            });
        });
    }

    /// <summary>
    /// Convenience wrapper that performs a left click (press + release) at a point.
    /// </summary>
    [McpServerTool, Description(
        "Synthetic LEFT click (pointer down + up) at window coordinates for peer-less controls. " +
        "(x,y) are inside the target top level. Optionally pass a control handle or selector to choose the window.")]
    public static Task<object> ClickAt(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("X coordinate within the target top level.")] double x,
        [Description("Y coordinate within the target top level.")] double y,
        [Description("Optional control handle whose window should receive the click.")] string? handle = null,
        [Description("Optional selector (first match) to pick the target window when no handle is given.")] string? selector = null)
        => Pointer(registry, ui, "click", x, y, handle, selector);

    /// <summary>
    /// Synthesizes a mouse-wheel scroll at a point by raising a pointer-wheel event on
    /// the hit-tested element.
    /// </summary>
    [McpServerTool, Description(
        "Synthetic mouse-wheel scroll at window coordinates. deltaX/deltaY are wheel notches " +
        "(positive deltaY scrolls up/away, negative scrolls down/toward you — Avalonia convention). " +
        "(x,y) are inside the target top level. Optionally choose the window by handle or selector.")]
    public static async Task<object> ScrollAt(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("X coordinate within the target top level.")] double x,
        [Description("Y coordinate within the target top level.")] double y,
        [Description("Horizontal wheel delta (notches). Usually 0.")] double deltaX = 0,
        [Description("Vertical wheel delta (notches). Positive = scroll up/away, negative = scroll down.")] double deltaY = -3,
        [Description("Optional control handle whose window should receive the scroll.")] string? handle = null,
        [Description("Optional selector (first match) to pick the target window when no handle is given.")] string? selector = null)
    {
        return await UiDispatch.Run(() =>
        {
            if (!TryResolveTopLevel(registry, ui, handle, selector, out var topLevel, out var error))
                return Fail(error);

            var target = ui.SendWheel(topLevel, new Point(x, y), new Vector(deltaX, deltaY));
            if (target is null)
                return Fail($"no control hit-tested at ({x}, {y}) in the target top level.");

            return Ok(new
            {
                x,
                y,
                deltaX,
                deltaY,
                target = registry.Assign(target),
                targetType = ui.GetTypeName(target),
            });
        });
    }

    /// <summary>
    /// Routes a string as text input to the focused element (or to an explicitly
    /// targeted control, which is focused first).
    /// </summary>
    [McpServerTool, Description(
        "Type literal text by raising TextInput events on the focused element. " +
        "If a handle/selector is given, that control is focused first. " +
        "This is the right tool for entering characters into custom text editors; for navigation/control keys use send_keys.")]
    public static async Task<object> TypeText(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("The literal text to type.")] string text,
        [Description("Optional control handle to focus before typing.")] string? handle = null,
        [Description("Optional selector (first match) to focus before typing.")] string? selector = null)
    {
        if (text is null)
            return Fail("text was null.");

        return await UiDispatch.Run(() =>
        {
            var target = ResolveFocusTarget(registry, handle, selector, out var error);
            if (error is not null)
                return Fail(error);

            var sink = ui.SendText(target, text);
            if (sink is null)
                return Fail("no focused element to receive text input (focus a control via handle/selector first).");

            return Ok(new
            {
                text,
                target = registry.Assign(sink),
                targetType = ui.GetTypeName(sink),
            });
        });
    }

    /// <summary>
    /// Sends one or more key chords (e.g. <c>Ctrl+S</c>, <c>Enter</c>, <c>Down</c>) to the
    /// focused element by raising KeyDown + KeyUp routed events.
    /// </summary>
    [McpServerTool, Description(
        "Send key combinations to the focused element as KeyDown/KeyUp events. " +
        "Each chord is modifiers + a key, e.g. \"Ctrl+S\", \"Shift+Tab\", \"Enter\", \"Escape\", \"Down\". " +
        "Pass several chords separated by spaces or commas (e.g. \"Ctrl+A Delete\"). " +
        "Optionally focus a control first via handle/selector. Use type_text for literal characters.")]
    public static async Task<object> SendKeys(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("One or more key chords, separated by spaces or commas. E.g. \"Ctrl+S\" or \"Down Down Enter\".")] string keys,
        [Description("Optional control handle to focus before sending keys.")] string? handle = null,
        [Description("Optional selector (first match) to focus before sending keys.")] string? selector = null)
    {
        if (string.IsNullOrWhiteSpace(keys))
            return Fail("keys was empty.");

        return await UiDispatch.Run(() =>
        {
            var target = ResolveFocusTarget(registry, handle, selector, out var resolveError);
            if (resolveError is not null)
                return Fail(resolveError);

            if (!ui.SendKeys(target, keys, out var sentChords, out var sink, out var error))
                return Fail(error);

            return Ok(new
            {
                sent = sentChords.ToArray(),
                target = sink is null ? null : registry.Assign(sink),
                targetType = sink is null ? null : ui.GetTypeName(sink),
            });
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Resolves the target <see cref="TopLevel"/> for pointer/scroll input. Order:
    /// the window containing the resolved handle, else the window of the first
    /// selector match, else the first open top level.
    /// </summary>
    private static bool TryResolveTopLevel(
        ControlRegistry registry, IUiAdapter ui, string? handle, string? selector,
        out TopLevel topLevel, out string error)
    {
        topLevel = null!;
        error = string.Empty;

        Control? anchor = null;
        if (!string.IsNullOrWhiteSpace(handle))
        {
            if (!registry.TryResolve(handle!, out anchor) || anchor is null)
            {
                error = $"handle '{handle}' did not resolve to a live control.";
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(selector))
        {
            anchor = registry.Query(selector!).FirstOrDefault();
            if (anchor is null)
            {
                error = $"selector '{selector}' matched no controls.";
                return false;
            }
        }

        if (anchor is not null)
        {
            var tl = ui.GetTopLevel(anchor);
            if (tl is null)
            {
                error = "the addressed control is not attached to a top level (window not shown?).";
                return false;
            }
            topLevel = tl;
            return true;
        }

        // No anchor: take the first open top level.
        var first = ui.EnumerateRoots().OfType<TopLevel>().FirstOrDefault();
        if (first is null)
        {
            error = "no open top level (window) is available to receive input.";
            return false;
        }

        topLevel = first;
        return true;
    }

    /// <summary>
    /// Resolves an explicit control to focus before keyboard/text input, or null to
    /// use the currently-focused element. Returns a non-null error string on a bad
    /// handle/selector.
    /// </summary>
    private static Control? ResolveFocusTarget(
        ControlRegistry registry, string? handle, string? selector, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(handle))
        {
            if (!registry.TryResolve(handle!, out var c) || c is null)
            {
                error = $"handle '{handle}' did not resolve to a live control.";
                return null;
            }
            return c;
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            var match = registry.Query(selector!).FirstOrDefault();
            if (match is null)
            {
                error = $"selector '{selector}' matched no controls.";
                return null;
            }
            return match;
        }

        return null;
    }

    private static bool TryMapPointerAction(string act, out PointerAction action)
    {
        switch (act)
        {
            case "move": action = PointerAction.Move; return true;
            case "down": action = PointerAction.Down; return true;
            case "up": action = PointerAction.Up; return true;
            case "click": action = PointerAction.Click; return true;
            case "double": action = PointerAction.DoubleClick; return true;
            case "right": action = PointerAction.RightClick; return true;
            default: action = PointerAction.Move; return false;
        }
    }

    private static object Ok(object payload) => new ResultEnvelope(true, null, payload);

    private static object Fail(string error) => new ResultEnvelope(false, error, null);

    /// <summary>
    /// Stable JSON shape for tool results: <c>{ ok, error, result }</c>.
    /// </summary>
    private sealed record ResultEnvelope(bool ok, string? error, object? result);
}
