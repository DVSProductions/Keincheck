using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using ModelContextProtocol.Server;

namespace Keincheck.Core.Tools;

/// <summary>
/// Read-only inspection tools: enumerate windows, walk the logical/visual tree,
/// query controls by selector, read properties, the data context, flattened
/// text, recent binding errors, and resolve a control by hit-test or current
/// keyboard focus. Every framework-specific operation is routed through
/// <see cref="IUiAdapter"/>; the tool bodies only resolve controls (via
/// <see cref="ControlRegistry"/>) and shape results. All UI access is marshalled
/// onto the Avalonia UI thread through <see cref="UiDispatch"/>; bad handles and
/// selectors are reported as structured results rather than thrown. Tree dumps are
/// filtered and depth-/page-capped by default so a single call cannot flood the
/// model with tokens.
/// </summary>
[McpServerToolType]
public static class InspectionTools
{
    // Hard ceilings so a caller cannot accidentally request an unbounded dump.
    private const int DefaultTreeDepth = 6;
    private const int MaxTreeDepth = 64;
    private const int DefaultTake = 200;
    private const int MaxTake = 2000;
    private const int PropSummaryCount = 6;

    // ----------------------------------------------------------------- list_windows

    /// <summary>
    /// Lists every open top-level (desktop windows, popups, or the single-view
    /// root) with a stable handle, title, type, bounds, and active state.
    /// </summary>
    [McpServerTool(Name = "list_windows"),
     Description("List all open top-levels (windows / popups / single-view root) with id, title, type, bounds and active state.")]
    public static async Task<object> ListWindows(ControlRegistry registry, IUiAdapter ui) =>
        await UiDispatch.Run<object>(() =>
        {
            var windows = new List<object>();
            foreach (var root in ui.EnumerateRoots())
            {
                if (root is not Control control)
                    continue;

                windows.Add(new
                {
                    id = registry.Assign(control),
                    title = ui.GetTitle(control),
                    type = ui.GetTypeName(control),
                    bounds = BoundsOf(ui, control),
                    isActive = ui.IsActiveWindow(control),
                });
            }

            return new { count = windows.Count, windows };
        });

    // ------------------------------------------------------------- get_logical_tree

    /// <summary>
    /// Returns the logical-tree subtree rooted at a control (by handle or
    /// selector, default: all open top-levels), filtered and capped.
    /// </summary>
    [McpServerTool(Name = "get_logical_tree"),
     Description("Dump the LOGICAL tree under a control/window (by handle or selector; default all top-levels). Filtered + depth/page-capped to avoid huge dumps.")]
    public static async Task<object> GetLogicalTree(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Control handle to root the dump at. Mutually exclusive with selector; takes priority.")] string? handle = null,
        [Description("Selector to root the dump at (first match). Omit both to dump every open top-level.")] string? selector = null,
        [Description("Maximum tree depth to descend. Default 6, hard max 64.")] int maxDepth = DefaultTreeDepth,
        [Description("When true, skip controls that are not effectively visible. Default false.")] bool visibleOnly = false,
        [Description("When true, skip controls that are not effectively enabled. Default false.")] bool enabledOnly = false,
        [Description("Number of root nodes to skip (pagination over the chosen roots). Default 0.")] int skip = 0,
        [Description("Number of root nodes to return. Default 200, hard max 2000.")] int take = DefaultTake) =>
        await DumpTree(registry, ui, handle, selector, maxDepth, visibleOnly, enabledOnly, skip, take, logical: true);

    // -------------------------------------------------------------- get_visual_tree

    /// <summary>
    /// Returns the visual-tree subtree rooted at a control (by handle or
    /// selector, default: all open top-levels), filtered and capped. Includes
    /// template-generated parts that the logical tree omits.
    /// </summary>
    [McpServerTool(Name = "get_visual_tree"),
     Description("Dump the VISUAL tree under a control/window (by handle or selector; default all top-levels). Includes template parts. Filtered + depth/page-capped.")]
    public static async Task<object> GetVisualTree(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Control handle to root the dump at. Mutually exclusive with selector; takes priority.")] string? handle = null,
        [Description("Selector to root the dump at (first match). Omit both to dump every open top-level.")] string? selector = null,
        [Description("Maximum tree depth to descend. Default 6, hard max 64.")] int maxDepth = DefaultTreeDepth,
        [Description("When true, skip controls that are not effectively visible. Default false.")] bool visibleOnly = false,
        [Description("When true, skip controls that are not effectively enabled. Default false.")] bool enabledOnly = false,
        [Description("Number of root nodes to skip (pagination over the chosen roots). Default 0.")] int skip = 0,
        [Description("Number of root nodes to return. Default 200, hard max 2000.")] int take = DefaultTake) =>
        await DumpTree(registry, ui, handle, selector, maxDepth, visibleOnly, enabledOnly, skip, take, logical: false);

    // -------------------------------------------------------------- query_controls

    /// <summary>
    /// Evaluates a CSS-ish selector and returns matching controls with handle,
    /// type, name, and bounds.
    /// </summary>
    [McpServerTool(Name = "query_controls"),
     Description("Resolve a CSS-ish selector and return matching controls as [{ id, type, name, bounds }].")]
    public static async Task<object> QueryControls(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("CSS-ish selector, e.g. \"Button[Name=ok]\", \"#submit\", \"StackPanel > TextBox\".")] string selector,
        [Description("Optional handle of a control whose top-level scopes the search. Omit to search all top-levels.")] string? scopeHandle = null,
        [Description("Number of matches to skip (pagination). Default 0.")] int skip = 0,
        [Description("Number of matches to return. Default 200, hard max 2000.")] int take = DefaultTake)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return Error("A selector is required.");

        return await UiDispatch.Run<object>(() =>
        {
            TopLevel? scope = null;
            if (!string.IsNullOrWhiteSpace(scopeHandle))
            {
                if (!registry.TryResolve(scopeHandle, out var scopeControl) || scopeControl is null)
                    return Error($"Unknown or collected scope handle '{scopeHandle}'.");
                scope = ui.GetTopLevel(scopeControl);
            }

            var matches = registry.Query(selector, scope);
            var total = matches.Count;
            var page = Page(matches, skip, take);

            var items = page.Select(c => (object)new
            {
                id = registry.Assign(c),
                type = ui.GetTypeName(c),
                name = ui.GetName(c),
                bounds = BoundsOf(ui, c),
            }).ToArray();

            return new { count = items.Length, total, skip = Math.Max(0, skip), controls = items };
        });
    }

    // -------------------------------------------------------------- get_properties

    /// <summary>
    /// Reads every registered styled/attached Avalonia property (plus a handful
    /// of common CLR properties) of a control, serialized to JSON-friendly form.
    /// </summary>
    [McpServerTool(Name = "get_properties"),
     Description("Read all registered styled/attached Avalonia properties (+ common CLR props) of a control. Address by handle or selector.")]
    public static async Task<object> GetProperties(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Control handle. Mutually exclusive with selector; takes priority.")] string? handle = null,
        [Description("Selector resolving to exactly one control (used when handle is omitted).")] string? selector = null) =>
        await UiDispatch.Run<object>(() =>
        {
            var resolved = Resolve(registry, handle, selector);
            if (resolved.Error is not null)
                return resolved.Error;

            var control = resolved.Control!;
            var styled = new SortedDictionary<string, object?>(StringComparer.Ordinal);

            foreach (var prop in ui.GetRegisteredProperties(control))
            {
                // Stable, de-duplicated by property name; readable values only.
                if (styled.ContainsKey(prop.Name))
                    continue;
                try
                {
                    styled[prop.Name] = ui.ReadProperty(control, prop);
                }
                catch
                {
                    styled[prop.Name] = null;
                }
            }

            var clr = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var name in CommonClrProps)
            {
                if (styled.ContainsKey(name))
                    continue;
                var value = ui.ReadProperty(control, name);
                if (value is not null)
                    clr[name] = value;
            }

            return new
            {
                ok = true,
                handle = registry.Assign(control),
                type = ui.GetTypeName(control),
                name = ui.GetName(control),
                styledCount = styled.Count,
                styled,
                clr,
            };
        });

    // ----------------------------------------------------------------- get_property

    /// <summary>Reads a single named property of a control.</summary>
    [McpServerTool(Name = "get_property"),
     Description("Read one named property of a control (styled or CLR). Address by handle or selector.")]
    public static async Task<object> GetProperty(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Property name, e.g. \"Width\", \"Background\", \"IsEnabled\".")] string propertyName,
        [Description("Control handle. Mutually exclusive with selector; takes priority.")] string? handle = null,
        [Description("Selector resolving to exactly one control (used when handle is omitted).")] string? selector = null)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return Error("A property name is required.");

        return await UiDispatch.Run<object>(() =>
        {
            var resolved = Resolve(registry, handle, selector);
            if (resolved.Error is not null)
                return resolved.Error;

            var control = resolved.Control!;
            var id = registry.Assign(control);

            // Prefer the styled/attached property of that name (more values are
            // reachable that way), falling back to a CLR property read.
            var avProp = ui.GetRegisteredProperties(control)
                .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.Ordinal));

            object? value;
            string source;
            if (avProp is not null)
            {
                value = ui.ReadProperty(control, avProp);
                source = "styled";
            }
            else
            {
                value = ui.ReadProperty(control, propertyName);
                source = "clr";
                if (value is null)
                    return Error($"Property '{propertyName}' was not found or is not readable on {ui.GetTypeName(control)}.",
                        new { handle = id });
            }

            return new { ok = true, handle = id, property = propertyName, source, value };
        });
    }

    // ------------------------------------------------------------- get_data_context

    /// <summary>
    /// Serializes the control's <c>DataContext</c> (depth-limited and cycle-safe
    /// via the adapter's property serializer).
    /// </summary>
    [McpServerTool(Name = "get_data_context"),
     Description("Serialize a control's DataContext (depth-limited, cycle-safe). Address by handle or selector.")]
    public static async Task<object> GetDataContext(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Control handle. Mutually exclusive with selector; takes priority.")] string? handle = null,
        [Description("Selector resolving to exactly one control (used when handle is omitted).")] string? selector = null) =>
        await UiDispatch.Run<object>(() =>
        {
            var resolved = Resolve(registry, handle, selector);
            if (resolved.Error is not null)
                return resolved.Error;

            var control = resolved.Control!;
            var id = registry.Assign(control);

            // The serializer projects "DataContext" depth-limited and cycle-safe;
            // a null projection means there is no data context.
            var value = ui.ReadProperty(control, "DataContext");
            if (value is null)
                return new { ok = true, handle = id, hasDataContext = false, type = (string?)null, dataContext = (object?)null };

            return new { ok = true, handle = id, hasDataContext = true, type = TypeNameOf(value), dataContext = value };
        });

    // ----------------------------------------------------------------- get_text

    /// <summary>
    /// Walks a subtree and concatenates the visible text it finds (the <c>Text</c>
    /// and string <c>Content</c> of the controls), in document order.
    /// </summary>
    [McpServerTool(Name = "get_text"),
     Description("Flatten the visible text of a control subtree (TextBlock/TextBox text + string ContentControl content). Address by handle or selector.")]
    public static async Task<object> GetText(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Control handle to root the walk at. Mutually exclusive with selector; takes priority.")] string? handle = null,
        [Description("Selector resolving to exactly one control (used when handle is omitted).")] string? selector = null,
        [Description("When true, skip controls that are not effectively visible. Default true.")] bool visibleOnly = true) =>
        await UiDispatch.Run<object>(() =>
        {
            var resolved = Resolve(registry, handle, selector);
            if (resolved.Error is not null)
                return resolved.Error;

            var control = resolved.Control!;
            var id = registry.Assign(control);

            var parts = new List<string>();
            CollectText(ui, control, visibleOnly, parts, new HashSet<Visual>(ReferenceEqualityComparer.Instance));

            var text = string.Join(" ", parts);
            return new { ok = true, handle = id, fragments = parts.Count, text };
        });

    // ----------------------------------------------------------- get_binding_errors

    /// <summary>Returns the most-recent captured Avalonia binding errors, oldest first.</summary>
    [McpServerTool(Name = "get_binding_errors"),
     Description("Return recent captured Avalonia binding errors (oldest first). n<=0 returns all buffered.")]
    public static Task<object> GetBindingErrors(
        IUiAdapter ui,
        [Description("Maximum number of errors to return (most recent). 0 or negative returns all buffered. Default 50.")] int n = 50)
    {
        var errors = ui.GetRecentBindingErrors(n, out var enabled);

        // Binding-error capture is only wired up when McpServerOptions.CaptureBindingErrors is true.
        if (!enabled)
            return Task.FromResult<object>(new
            {
                ok = true,
                enabled = false,
                count = 0,
                errors = Array.Empty<string>(),
                note = "Binding-error capture is disabled (McpServerOptions.CaptureBindingErrors == false).",
            });

        return Task.FromResult<object>(new { ok = true, enabled = true, count = errors.Count, errors });
    }

    // ----------------------------------------------------------------- hit_test

    /// <summary>
    /// Returns the deepest control at a point in a top-level's client coordinates.
    /// </summary>
    [McpServerTool(Name = "hit_test"),
     Description("Find the control at an (x,y) point in top-level client coordinates. Scope the top-level by handle, else the first/active window.")]
    public static async Task<object> HitTest(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("X coordinate in the top-level's client space (device-independent pixels).")] double x,
        [Description("Y coordinate in the top-level's client space (device-independent pixels).")] double y,
        [Description("Handle of any control in the target window; its top-level is hit-tested. Omit to use the first open top-level.")] string? handle = null) =>
        await UiDispatch.Run<object>(() =>
        {
            TopLevel? top;
            if (!string.IsNullOrWhiteSpace(handle))
            {
                if (!registry.TryResolve(handle, out var anchor) || anchor is null)
                    return Error($"Unknown or collected handle '{handle}'.");
                top = ui.GetTopLevel(anchor);
                if (top is null)
                    return Error("The addressed control is not attached to a top-level.", new { handle });
            }
            else
            {
                top = ui.EnumerateRoots().OfType<TopLevel>().FirstOrDefault();
                if (top is null)
                    return Error("No open top-level is available to hit-test.");
            }

            var control = ui.HitTest(top, new Point(x, y));
            if (control is null)
                return new { ok = true, hit = false, point = new { x, y }, handle = (string?)null };

            return new
            {
                ok = true,
                hit = true,
                point = new { x, y },
                handle = registry.Assign(control),
                type = ui.GetTypeName(control),
                name = ui.GetName(control),
                bounds = BoundsOf(ui, control),
            };
        });

    // ----------------------------------------------------------- get_focused_element

    /// <summary>Returns the control currently holding keyboard focus, if any.</summary>
    [McpServerTool(Name = "get_focused_element"),
     Description("Return the control that currently holds keyboard focus. Scope by handle's top-level, else the first open top-level.")]
    public static async Task<object> GetFocusedElement(
        ControlRegistry registry,
        IUiAdapter ui,
        [Description("Handle of any control in the target window; its top-level's focus manager is queried. Omit to use the first open top-level.")] string? handle = null) =>
        await UiDispatch.Run<object>(() =>
        {
            TopLevel? top;
            if (!string.IsNullOrWhiteSpace(handle))
            {
                if (!registry.TryResolve(handle, out var anchor) || anchor is null)
                    return Error($"Unknown or collected handle '{handle}'.");
                top = ui.GetTopLevel(anchor);
                if (top is null)
                    return Error("The addressed control is not attached to a top-level.", new { handle });
            }
            else
            {
                top = ui.EnumerateRoots().OfType<TopLevel>().FirstOrDefault();
                if (top is null)
                    return Error("No open top-level is available.");
            }

            var control = ui.GetFocusedElement(top);
            if (control is null)
                return new { ok = true, focused = false, handle = (string?)null };

            return new
            {
                ok = true,
                focused = true,
                handle = registry.Assign(control),
                type = ui.GetTypeName(control),
                name = ui.GetName(control),
                bounds = BoundsOf(ui, control),
            };
        });

    // ----------------------------------------------------------------- tree helpers

    private static async Task<object> DumpTree(
        ControlRegistry registry,
        IUiAdapter ui,
        string? handle,
        string? selector,
        int maxDepth,
        bool visibleOnly,
        bool enabledOnly,
        int skip,
        int take,
        bool logical) =>
        await UiDispatch.Run<object>(() =>
        {
            var depth = Math.Clamp(maxDepth <= 0 ? DefaultTreeDepth : maxDepth, 1, MaxTreeDepth);

            // Choose the set of roots: an explicit control, or all top-levels.
            List<Control> roots;
            if (!string.IsNullOrWhiteSpace(handle) || !string.IsNullOrWhiteSpace(selector))
            {
                var resolved = Resolve(registry, handle, selector);
                if (resolved.Error is not null)
                    return resolved.Error;
                roots = new List<Control> { resolved.Control! };
            }
            else
            {
                roots = ui.EnumerateRoots().OfType<Control>().ToList();
            }

            var total = roots.Count;
            var page = Page(roots, skip, take);

            // A reference-type flag so the recursion can flip it from inside
            // lambdas (a `ref bool` cannot be captured by a lambda in C#).
            var truncated = new Flag();
            var nodes = page
                .Select(r => BuildNode(registry, ui, r, depth, visibleOnly, enabledOnly, logical, truncated))
                .Where(n => n is not null)
                .ToArray();

            return new
            {
                ok = true,
                tree = logical ? "logical" : "visual",
                rootCount = total,
                skip = Math.Max(0, skip),
                returned = nodes.Length,
                maxDepth = depth,
                truncated = truncated.Value,
                nodes,
            };
        });

    private sealed class Flag { public bool Value; }

    private static object? BuildNode(
        ControlRegistry registry,
        IUiAdapter ui,
        Control control,
        int remainingDepth,
        bool visibleOnly,
        bool enabledOnly,
        bool logical,
        Flag truncated)
    {
        if (visibleOnly && !ui.IsEffectivelyVisible(control))
            return null;
        if (enabledOnly && !ui.IsEffectivelyEnabled(control))
            return null;

        // Materialize matching child controls (filters applied) up front so we
        // can report an accurate childCount even when depth runs out.
        var childControls = ChildControls(ui, control, logical)
            .Where(c => (!visibleOnly || ui.IsEffectivelyVisible(c)) && (!enabledOnly || ui.IsEffectivelyEnabled(c)))
            .ToList();

        object?[] children;
        if (remainingDepth <= 1)
        {
            if (childControls.Count > 0)
                truncated.Value = true;
            children = Array.Empty<object?>();
        }
        else
        {
            children = childControls
                .Select(c => BuildNode(registry, ui, c, remainingDepth - 1, visibleOnly, enabledOnly, logical, truncated))
                .Where(n => n is not null)
                .ToArray();
        }

        return new
        {
            id = registry.Assign(control),
            type = ui.GetTypeName(control),
            name = ui.GetName(control),
            bounds = BoundsOf(ui, control),
            childCount = childControls.Count,
            props = PropSummary(ui, control),
            children,
        };
    }

    private static IEnumerable<Control> ChildControls(IUiAdapter ui, Control control, bool logical) =>
        logical ? ui.GetLogicalChildren(control) : ui.GetVisualChildren(control);

    /// <summary>
    /// A tiny, predictable key-prop summary for tree nodes: a few high-signal
    /// properties when present, serialized JSON-friendly. Kept small on purpose.
    /// </summary>
    private static Dictionary<string, object?> PropSummary(IUiAdapter ui, Control control)
    {
        var summary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in SummaryProps)
        {
            if (summary.Count >= PropSummaryCount)
                break;
            var value = ui.ReadProperty(control, name);
            if (value is null)
                continue;
            // Skip empty strings to keep the summary terse.
            if (value is string s && s.Length == 0)
                continue;
            summary[name] = value;
        }
        return summary;
    }

    // ----------------------------------------------------------------- text helpers

    private static void CollectText(IUiAdapter ui, Control control, bool visibleOnly, List<string> sink, HashSet<Visual> visited)
    {
        // Global visited guard: the merged logical+visual graph can contain cycles
        // (popup/overlay/adorner cross-links between the two trees), so without this a
        // real app's UI sends this recursion into a StackOverflow that hard-kills the
        // process. It also de-duplicates diamond paths so text is collected once.
        if (!visited.Add(control))
            return;

        if (visibleOnly && !ui.IsEffectivelyVisible(control))
            return;

        // Text-carrying controls expose either a CLR "Text" property (TextBlock,
        // TextBox, …) or a string "Content" (ContentControl). The serializer renders
        // both as a plain string, so a non-empty string read of either is real text.
        if (ui.ReadProperty(control, "Text") is string t && t.Length > 0)
            sink.Add(t);
        else if (ui.ReadProperty(control, "Content") is string cs && cs.Length > 0)
            sink.Add(cs);

        // Merge logical + visual children (template parts carry text too) so e.g. a
        // Button's templated TextBlock is reached; the shared visited set dedups them.
        foreach (var c in ui.GetLogicalChildren(control))
            CollectText(ui, c, visibleOnly, sink, visited);
        foreach (var c in ui.GetVisualChildren(control))
            CollectText(ui, c, visibleOnly, sink, visited);
    }

    // --------------------------------------------------------------- small helpers

    private static object BoundsOf(IUiAdapter ui, Control c)
    {
        var b = ui.GetBounds(c);
        return new { x = b.X, y = b.Y, width = b.Width, height = b.Height };
    }

    /// <summary>
    /// Best-effort type label for a serialized DataContext value. The serializer
    /// flattens an opaque view-model to its <c>ToString()</c> (a string) or a
    /// collection, so the original CLR type is no longer recoverable through the
    /// seam; report null in those cases rather than the misleading projection type.
    /// Only a preserved scalar/value projection yields a concrete type name.
    /// </summary>
    private static string? TypeNameOf(object value) =>
        value is string or System.Collections.IEnumerable ? null : value.GetType().FullName;

    private static IReadOnlyList<T> Page<T>(IReadOnlyList<T> source, int skip, int take)
    {
        var start = Math.Max(0, skip);
        var count = Math.Clamp(take <= 0 ? DefaultTake : take, 1, MaxTake);
        if (start >= source.Count)
            return Array.Empty<T>();
        var end = Math.Min(source.Count, start + count);
        var result = new List<T>(end - start);
        for (var i = start; i < end; i++)
            result.Add(source[i]);
        return result;
    }

    /// <summary>
    /// Resolves a control from a handle (preferred) or a selector that must match
    /// exactly one control. MUST be called on the UI thread.
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

    private readonly struct Resolution
    {
        public Control? Control { get; init; }
        public object? Error { get; init; }
    }

    private static object Error(string message, object? extra = null) =>
        extra is null
            ? new { ok = false, error = message }
            : Merge(new { ok = false, error = message }, extra);

    /// <summary>
    /// Shallow-merges two anonymous objects into a JSON-serializable dictionary so
    /// callers receive a single flat result object.
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

    // High-signal CLR properties appended to get_properties when not already a
    // styled property of the same name.
    private static readonly string[] CommonClrProps =
    {
        "Name", "Classes", "DataContext", "TemplatedParent", "Parent",
    };

    // Compact per-node summary keys for tree dumps (first PropSummaryCount present).
    private static readonly string[] SummaryProps =
    {
        "Text", "Content", "Header", "IsVisible", "IsEnabled", "IsChecked",
        "Value", "SelectedItem", "Background", "Foreground",
    };
}
