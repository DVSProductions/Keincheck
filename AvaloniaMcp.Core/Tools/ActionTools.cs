using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using ModelContextProtocol.Server;

namespace AvaloniaMcp.Core.Tools;

/// <summary>
/// Mutating / semantic tools: set a property, drive a control through its
/// UI-Automation peer, move keyboard focus, and wait for a UI condition. Every
/// framework-specific operation is routed through <see cref="IUiAdapter"/>; the tool
/// bodies only resolve controls (via <see cref="ControlRegistry"/>) and shape
/// results. Visual-tree access is marshalled onto the Avalonia UI thread through
/// <see cref="UiDispatch"/>; bad handles, selectors, and coercion failures are
/// reported as structured results rather than thrown.
/// </summary>
[McpServerToolType]
public static class ActionTools
{
    /// <summary>
    /// Explicit automation action a caller may request instead of relying on
    /// auto-detection of the control's supported pattern.
    /// </summary>
    public enum AutomationActionKind
    {
        /// <summary>Auto-detect the right pattern from the control's peer.</summary>
        Auto = 0,
        /// <summary>Invoke (push) the control.</summary>
        Invoke,
        /// <summary>Toggle the control.</summary>
        Toggle,
        /// <summary>Set the control's text/value. Requires <c>value</c>.</summary>
        SetValue,
        /// <summary>Expand the control.</summary>
        Expand,
        /// <summary>Collapse the control.</summary>
        Collapse,
        /// <summary>Select the item.</summary>
        Select,
    }

    // ----------------------------------------------------------------- set_property

    /// <summary>
    /// Sets a styled/CLR property on a control by name from a JSON value, using the
    /// adapter's property writer for coercion (e.g. <c>"10,5,10,5"</c> →
    /// <see cref="Avalonia.Thickness"/>). Address the control by a handle (preferred)
    /// or a selector that resolves to exactly one match.
    /// </summary>
    [McpServerTool(Name = "set_property"),
     Description("Set a control property by name from a JSON value (with type coercion). Address by handle or selector.")]
    public static async Task<object> SetProperty(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Control handle (e.g. \"ctl-1a\"). Mutually exclusive with selector; takes priority.")] string? handle,
        [Description("CSS-ish selector that must resolve to exactly one control (used when handle is omitted).")] string? selector,
        [Description("Property name to set, e.g. \"Width\", \"Background\", \"Margin\", \"IsEnabled\".")] string propertyName,
        [Description("New value as JSON (string/number/bool). Strings are coerced via TypeConverter/Parse, e.g. \"#FF0000\" or \"10,5,10,5\".")] JsonElement value)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return Error("Property name is required.");

        return await UiDispatch.Run<object>(() =>
        {
            var resolved = Resolve(registry, handle, selector);
            if (resolved.Error is not null)
                return resolved.Error;

            var control = resolved.Control!;
            if (ui.WriteProperty(control, propertyName, value, out var error))
            {
                return new
                {
                    ok = true,
                    handle = registry.Assign(control),
                    property = propertyName,
                    newValue = ui.ReadProperty(control, propertyName),
                };
            }

            return Error(error, new { handle = registry.Assign(control), property = propertyName });
        });
    }

    // ------------------------------------------------------------ automation_action

    /// <summary>
    /// Performs a semantic action on a control through its UI-Automation peer.
    /// When <paramref name="action"/> is <see cref="AutomationActionKind.Auto"/>
    /// the first supported pattern is used in priority order:
    /// Invoke → Toggle → ExpandCollapse → SelectionItem → Value. Provide
    /// <paramref name="value"/> for an explicit <c>SetValue</c>.
    /// </summary>
    [McpServerTool(Name = "automation_action"),
     Description("Drive a control via its UI Automation peer: invoke/toggle/set-value/expand/collapse/select. Auto-detects the pattern unless an explicit action is given.")]
    public static async Task<object> AutomationAction(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Control handle (e.g. \"ctl-1a\"). Mutually exclusive with selector; takes priority.")] string? handle,
        [Description("CSS-ish selector that must resolve to exactly one control (used when handle is omitted).")] string? selector,
        [Description("Explicit action: Auto, Invoke, Toggle, SetValue, Expand, Collapse, Select. Default Auto.")] AutomationActionKind action = AutomationActionKind.Auto,
        [Description("Value for SetValue (or Auto on a value-capable control). Ignored otherwise.")] string? value = null)
    {
        return await UiDispatch.Run<object>(() =>
        {
            var resolved = Resolve(registry, handle, selector);
            if (resolved.Error is not null)
                return resolved.Error;

            var control = resolved.Control!;
            var id = registry.Assign(control);

            var result = ui.InvokeAutomation(control, MapAction(action), value);
            if (!result.Ok)
                return Error(result.Error ?? "Automation action failed.", new { handle = id });

            // The adapter reports the pattern it used and an optional human-readable
            // state string; surface both alongside the handle.
            return result.State is null
                ? new { ok = true, handle = id, action = result.Action }
                : (object)new { ok = true, handle = id, action = result.Action, state = result.State };
        });
    }

    private static UiAutomationAction MapAction(AutomationActionKind kind) => kind switch
    {
        AutomationActionKind.Invoke   => UiAutomationAction.Invoke,
        AutomationActionKind.Toggle   => UiAutomationAction.Toggle,
        AutomationActionKind.SetValue => UiAutomationAction.SetValue,
        AutomationActionKind.Expand   => UiAutomationAction.Expand,
        AutomationActionKind.Collapse => UiAutomationAction.Collapse,
        AutomationActionKind.Select   => UiAutomationAction.Select,
        _                             => UiAutomationAction.Auto,
    };

    // ----------------------------------------------------------------- set_focus

    /// <summary>
    /// Moves keyboard focus to a control via the adapter. Reports whether the
    /// control actually became focused.
    /// </summary>
    [McpServerTool(Name = "set_focus"),
     Description("Move keyboard focus to a control (control.Focus()). Address by handle or selector.")]
    public static async Task<object> SetFocus(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Control handle (e.g. \"ctl-1a\"). Mutually exclusive with selector; takes priority.")] string? handle,
        [Description("CSS-ish selector that must resolve to exactly one control (used when handle is omitted).")] string? selector)
    {
        return await UiDispatch.Run<object>(() =>
        {
            var resolved = Resolve(registry, handle, selector);
            if (resolved.Error is not null)
                return resolved.Error;

            var control = resolved.Control!;
            var id = registry.Assign(control);

            var requested = ui.SetFocus(control);
            return new
            {
                ok = true,
                handle = id,
                focusRequested = requested,
                isFocused = ui.ReadProperty(control, "IsFocused") is true,
            };
        });
    }

    // ----------------------------------------------------------------- wait_for

    /// <summary>
    /// Polls the UI thread until one of two conditions is met or a timeout
    /// elapses: (a) a <paramref name="selector"/> matches at least one control,
    /// or (b) the named <paramref name="propertyName"/> of the control addressed
    /// by <paramref name="handle"/>/<paramref name="selector"/> JSON-equals
    /// <paramref name="expected"/>. Each poll runs a short snapshot on the UI
    /// thread; the wait between polls happens off the UI thread so the dispatcher
    /// is never blocked.
    /// </summary>
    [McpServerTool(Name = "wait_for"),
     Description("Poll until a selector matches OR a control property equals a JSON value OR timeout. Does not block the UI thread between polls.")]
    public static async Task<object> WaitFor(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Selector to wait for. If 'property' is omitted, succeeds once this selector matches anything.")] string? selector,
        [Description("Control handle for a property-equality wait (alternative to selector).")] string? handle = null,
        [Description("Property name to compare; when set, waits until it equals 'expected'.")] string? propertyName = null,
        [Description("Expected JSON value for the property comparison.")] JsonElement? expected = null,
        [Description("Maximum time to wait, milliseconds. Default 5000.")] int timeoutMs = 5000,
        [Description("Delay between polls, milliseconds. Default 100.")] int pollIntervalMs = 100)
    {
        if (string.IsNullOrWhiteSpace(selector) && string.IsNullOrWhiteSpace(handle))
            return Error("Provide a selector (existence wait) and/or a handle/selector + property (equality wait).");

        var wantProperty = !string.IsNullOrWhiteSpace(propertyName);
        if (wantProperty && expected is null or { ValueKind: JsonValueKind.Undefined })
            return Error("An 'expected' value is required when 'property' is set.");

        var effectiveTimeout = Math.Max(0, timeoutMs);
        var interval = Math.Max(1, pollIntervalMs);
        var sw = Stopwatch.StartNew();
        var attempts = 0;
        string? lastObserved = null;

        while (true)
        {
            attempts++;

            var outcome = await UiDispatch.Run<object?>(() =>
            {
                if (wantProperty)
                {
                    var resolved = Resolve(registry, handle, selector);
                    if (resolved.Control is null)
                        return null; // not resolvable yet — keep polling

                    var actual = ui.ReadProperty(resolved.Control, propertyName!);
                    if (JsonEquals(actual, expected!.Value))
                    {
                        return new
                        {
                            ok = true,
                            matched = "property",
                            handle = registry.Assign(resolved.Control),
                            property = propertyName,
                            value = actual,
                            attempts,
                            elapsedMs = (long)sw.Elapsed.TotalMilliseconds,
                        };
                    }

                    lastObserved = JsonSerializer.Serialize(actual);
                    return null;
                }

                // Existence wait.
                var matches = registry.Query(selector!);
                if (matches.Count > 0)
                {
                    var handles = matches.Select(registry.Assign).ToArray();
                    return new
                    {
                        ok = true,
                        matched = "selector",
                        count = handles.Length,
                        handles,
                        attempts,
                        elapsedMs = (long)sw.Elapsed.TotalMilliseconds,
                    };
                }

                return null;
            });

            if (outcome is not null)
                return outcome;

            if (sw.Elapsed.TotalMilliseconds >= effectiveTimeout)
            {
                return new
                {
                    ok = false,
                    error = "Timed out waiting for condition.",
                    matched = (string?)null,
                    timedOut = true,
                    waitedFor = wantProperty ? "property" : "selector",
                    attempts,
                    elapsedMs = (long)sw.Elapsed.TotalMilliseconds,
                    lastObserved,
                };
            }

            // Wait OFF the UI thread so the dispatcher stays responsive.
            await Task.Delay(interval).ConfigureAwait(false);
        }
    }

    // ----------------------------------------------------------------- helpers

    private readonly struct Resolution
    {
        public Control? Control { get; init; }
        public object? Error { get; init; }
    }

    /// <summary>
    /// Resolves a control from a handle (preferred) or a selector. The selector
    /// must match exactly one control. MUST be called on the UI thread.
    /// </summary>
    private static Resolution Resolve(ControlRegistry registry, string? handle, string? selector)
    {
        if (!string.IsNullOrWhiteSpace(handle))
        {
            if (registry.TryResolve(handle, out var byHandle) && byHandle is not null)
                return new Resolution { Control = byHandle };
            return new Resolution { Error = Error($"Unknown or collected handle '{handle}'.") };
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            var matches = registry.Query(selector);
            return matches.Count switch
            {
                1 => new Resolution { Control = matches[0] },
                0 => new Resolution { Error = Error($"Selector '{selector}' matched no controls.") },
                _ => new Resolution
                {
                    Error = Error(
                        $"Selector '{selector}' matched {matches.Count} controls; expected exactly one.",
                        new { handles = matches.Select(registry.Assign).ToArray() }),
                },
            };
        }

        return new Resolution { Error = Error("Provide either a handle or a selector.") };
    }

    private static object Error(string message, object? extra = null) =>
        extra is null
            ? new { ok = false, error = message }
            : Merge(new { ok = false, error = message }, extra);

    /// <summary>
    /// Shallow-merges two anonymous objects into a JSON-serializable dictionary
    /// so callers receive a single flat result object.
    /// </summary>
    private static object Merge(object baseObj, object extra)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in baseObj.GetType().GetProperties())
            dict[p.Name] = p.GetValue(baseObj);
        foreach (var p in extra.GetType().GetProperties())
            dict[p.Name] = p.GetValue(extra);
        return dict;
    }

    /// <summary>
    /// Compares a serializer-produced value (already JSON-friendly) against an
    /// expected <see cref="JsonElement"/> by round-tripping both through JSON.
    /// </summary>
    private static bool JsonEquals(object? actual, JsonElement expected)
    {
        try
        {
            var actualJson = JsonSerializer.SerializeToElement(actual);
            return JsonElementDeepEquals(actualJson, expected);
        }
        catch
        {
            return false;
        }
    }

    private static bool JsonElementDeepEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            // Allow string<->number/bool leniency: compare invariant text forms.
            return RawText(a) == RawText(b);
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                var bProps = b.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                if (aProps.Length != bProps.Length)
                    return false;
                for (var i = 0; i < aProps.Length; i++)
                {
                    if (aProps[i].Name != bProps[i].Name)
                        return false;
                    if (!JsonElementDeepEquals(aProps[i].Value, bProps[i].Value))
                        return false;
                }
                return true;

            case JsonValueKind.Array:
                var aItems = a.EnumerateArray().ToArray();
                var bItems = b.EnumerateArray().ToArray();
                if (aItems.Length != bItems.Length)
                    return false;
                for (var i = 0; i < aItems.Length; i++)
                {
                    if (!JsonElementDeepEquals(aItems[i], bItems[i]))
                        return false;
                }
                return true;

            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Number:
                return a.GetRawText() == b.GetRawText() ||
                       (a.TryGetDouble(out var ad) && b.TryGetDouble(out var bd) && ad.Equals(bd));

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;

            default:
                return a.GetRawText() == b.GetRawText();
        }
    }

    private static string RawText(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText();
}
