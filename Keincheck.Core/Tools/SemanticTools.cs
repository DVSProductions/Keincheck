using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Keincheck.Core.Tools;

/// <summary>
/// Accessibility-first situational-awareness tools. Instead of the raw logical/visual
/// type tree, these surface the UI the way a screen-reader (or a sighted operator) sees
/// it: an accessibility "semantic" tree of roles / names / states, a "set-of-marks"
/// screenshot whose interactive controls are drawn as numbered boxes with a JSON legend
/// mapping each number back to a real control handle, a one-call <c>describe_screen</c>
/// that bundles both, and a <c>wait_for_idle</c> that lets the model settle the UI after
/// acting. Every framework-specific operation is routed through <see cref="IUiAdapter"/>
/// (<see cref="IUiAdapter.GetSemanticInfo"/>, <see cref="IUiAdapter.TryGetBoundsInTopLevel"/>,
/// <see cref="IUiAdapter.TryRenderAnnotated"/>); the tool bodies only resolve elements (via
/// <see cref="ControlRegistry"/>), walk with a shared visited-guard, and shape results.
/// All UI access is marshalled onto the UI thread through <see cref="IUiDispatcher"/>; bad
/// handles and selectors are reported as structured results, never thrown. Depth/total caps
/// match <see cref="InspectionTools"/> so a single call cannot flood the model with tokens.
/// </summary>
[McpServerToolType]
public static class SemanticTools
{
    private const string PngMimeType = "image/png";

    // Mirrors InspectionTools' ceilings so the two tool sets behave consistently.
    private const int DefaultTreeDepth = 12;
    private const int MaxTreeDepth = 64;

    // A semantic tree node carries more per-node text than a raw type node, so the total
    // node budget is tighter than InspectionTools' page size to keep payloads bounded.
    private const int MaxNodes = 1500;

    // Default node budget for the shallow tree bundled into describe_screen.
    private const int DescribeMaxDepth = 4;

    // ----------------------------------------------------------- get_semantic_tree

    /// <summary>
    /// Walks a control subtree (by handle or selector; default: every open top-level) and
    /// emits a compact accessibility node per control — role, name, value, interactivity,
    /// states, bounds, children — optionally filtered to interactive controls only.
    /// </summary>
    [McpServerTool(Name = "get_semantic_tree"),
     Description("Dump the ACCESSIBILITY (semantic) tree under a control/window (by handle or selector; default all top-levels): per node { id, role, name, value, interactive, states, bounds, children }. Filter to interactiveOnly to see just actionable controls. Depth/total-capped.")]
    public static async Task<object> GetSemanticTree(
        ControlRegistry registry,
        IUiAdapter ui,
        IUiDispatcher dispatcher,
        [Description("Control handle to root the walk at. Mutually exclusive with selector; takes priority.")] string? handle = null,
        [Description("Selector to root the walk at (first match). Omit both to walk every open top-level.")] string? selector = null,
        [Description("Maximum tree depth to descend. Default 12, hard max 64.")] int maxDepth = DefaultTreeDepth,
        [Description("When true, emit only controls the adapter reports as interactive (clickable/editable/togglable). Default false.")] bool interactiveOnly = false) =>
        await dispatcher.Run<object>(() =>
        {
            var depth = Math.Clamp(maxDepth <= 0 ? DefaultTreeDepth : maxDepth, 1, MaxTreeDepth);

            // Choose roots: an explicit control, or every open top-level (control-only).
            List<object> roots;
            if (!string.IsNullOrWhiteSpace(handle) || !string.IsNullOrWhiteSpace(selector))
            {
                var resolved = Resolve(registry, ui, handle, selector);
                if (resolved.Error is not null)
                    return resolved.Error;
                roots = new List<object> { resolved.Control! };
            }
            else
            {
                roots = ui.EnumerateRoots().Where(ui.IsControl).ToList();
            }

            // ONE shared visited-guard across ALL roots and the merged logical+visual walk:
            // a real app's overlay/adorner cross-links make the merged graph cyclic, and an
            // unguarded walk StackOverflow-kills the host (see CollectText in InspectionTools).
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var budget = new NodeBudget(MaxNodes);

            var nodes = roots
                .Select(r => BuildSemanticNode(registry, ui, r, depth, interactiveOnly, visited, budget))
                .Where(n => n is not null)
                .ToArray();

            return new
            {
                ok = true,
                rootCount = roots.Count,
                interactiveOnly,
                maxDepth = depth,
                truncated = budget.Exhausted,
                returned = nodes.Length,
                nodes,
            };
        });

    // ----------------------------------------------------------- screenshot_marked

    /// <summary>
    /// Renders a top-level as a "set-of-marks" screenshot: every markable control is drawn
    /// as a numbered box, and a JSON legend maps each number to the control's real handle,
    /// role, name, and bounds so the model can act on a mark via the other tools.
    /// </summary>
    [McpServerTool(Name = "screenshot_marked"),
     Description("Render a window as a SET-OF-MARKS screenshot: markable controls get numbered boxes overlaid on the image, plus a JSON legend [{ mark, id, role, name, bounds }] mapping each number to a real control handle to act on. Target a window by handle/selector; default the first open top-level. interactiveOnly (default true) limits marks to actionable controls.")]
    public static Task<CallToolResult> ScreenshotMarked(
        ControlRegistry registry,
        IUiAdapter ui,
        IUiDispatcher dispatcher,
        McpServerOptions options,
        [Description("Control handle or selector identifying the window (or any control inside it). Omit to use the first open top-level.")] string? target = null,
        [Description("Maximum number of marks to draw (in document order). Default 60.")] int maxMarks = 60,
        [Description("When true (default), mark only controls the adapter reports as interactive; when false, mark anything with a name/role.")] bool interactiveOnly = true)
        => dispatcher.Run(() =>
        {
            if (!TryResolveTopLevel(registry, ui, target, out var topLevel, out var resolveError))
                return ErrorResult(resolveError);

            if (!TryRenderMarked(registry, ui, options, topLevel!, maxMarks, interactiveOnly,
                    out var picks, out var png, out var renderError))
                return ErrorResult(renderError);

            // Legend lets the model translate a mark number back into a usable handle.
            var legend = BuildLegend(picks);
            var legendJson = JsonSerializer.Serialize(new { ok = true, count = legend.Length, marks = legend });

            // TWO content blocks: the annotated image, then the legend text.
            return new CallToolResult
            {
                Content = new List<ContentBlock>
                {
                    ImageContentBlock.FromBytes(png, PngMimeType),
                    new TextContentBlock { Text = legendJson },
                },
            };
        });

    // -------------------------------------------------------------- describe_screen

    /// <summary>
    /// One-call situational awareness: the set-of-marks screenshot, its legend, AND a
    /// shallow semantic tree of the same top-level — everything the model needs to orient
    /// itself before acting, in a single round-trip.
    /// </summary>
    [McpServerTool(Name = "describe_screen"),
     Description("One-call situational awareness for a window: returns the SET-OF-MARKS screenshot (numbered interactive controls), its JSON legend mapping marks to handles, AND a shallow accessibility (semantic) summary. Target by handle/selector; default the first open top-level.")]
    public static Task<CallToolResult> DescribeScreen(
        ControlRegistry registry,
        IUiAdapter ui,
        IUiDispatcher dispatcher,
        McpServerOptions options,
        [Description("Control handle or selector identifying the window (or any control inside it). Omit to use the first open top-level.")] string? target = null,
        [Description("Maximum number of marks to draw (in document order). Default 60.")] int maxMarks = 60,
        [Description("When true (default), mark only interactive controls; when false, mark anything with a name/role.")] bool interactiveOnly = true,
        [Description("Depth of the bundled semantic summary. Default 4, hard max 64.")] int summaryDepth = DescribeMaxDepth)
        => dispatcher.Run(() =>
        {
            if (!TryResolveTopLevel(registry, ui, target, out var topLevel, out var resolveError))
                return ErrorResult(resolveError);

            // --- set-of-marks render (shared with screenshot_marked) ---
            if (!TryRenderMarked(registry, ui, options, topLevel!, maxMarks, interactiveOnly,
                    out var picks, out var png, out var renderError))
                return ErrorResult(renderError);

            var legend = BuildLegend(picks);

            // --- shallow semantic summary of the same top-level ---
            var depth = Math.Clamp(summaryDepth <= 0 ? DescribeMaxDepth : summaryDepth, 1, MaxTreeDepth);
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var budget = new NodeBudget(MaxNodes);
            var summaryRoot = BuildSemanticNode(registry, ui, topLevel!, depth, interactiveOnly: false, visited, budget);

            var summary = new
            {
                ok = true,
                window = new
                {
                    id = registry.Assign(topLevel!),
                    title = ui.GetTitle(topLevel!),
                    type = ui.GetTypeName(topLevel!),
                    bounds = RectJson(ui.GetBounds(topLevel!)),
                },
                markCount = legend.Length,
                marks = legend,
                semanticDepth = depth,
                semanticTruncated = budget.Exhausted,
                semantics = summaryRoot,
            };

            var summaryJson = JsonSerializer.Serialize(summary);

            return new CallToolResult
            {
                Content = new List<ContentBlock>
                {
                    ImageContentBlock.FromBytes(png, PngMimeType),
                    new TextContentBlock { Text = summaryJson },
                },
            };
        });

    // ----------------------------------------------------------------- wait_for_idle

    /// <summary>
    /// Awaits a UI-thread idle (layout/render drained) so a follow-up inspection observes a
    /// settled tree, bounded by <paramref name="timeoutMs"/>. Reports whether idle was
    /// reached before the timeout.
    /// </summary>
    [McpServerTool(Name = "wait_for_idle"),
     Description("Wait until the UI thread goes idle (pending layout/render drained) so the next inspection sees a settled tree. Bounded by timeoutMs. Returns { ok, idle } — idle=false means the timeout elapsed first.")]
    public static async Task<object> WaitForIdle(
        IUiDispatcher dispatcher,
        [Description("Maximum time to wait for idle, milliseconds. Default 2000.")] int timeoutMs = 2000)
    {
        var timeout = Math.Max(0, timeoutMs);
        var idleTask = dispatcher.WaitForIdle();

        // Race the idle round-trip against a timeout so a stuck UI thread cannot hang the
        // tool. We never block the calling (background) thread on the UI thread.
        var winner = await Task.WhenAny(idleTask, Task.Delay(timeout)).ConfigureAwait(false);
        var idle = ReferenceEquals(winner, idleTask) && idleTask.IsCompletedSuccessfully;

        return new { ok = true, idle, timeoutMs = timeout };
    }

    // ------------------------------------------------------------ semantic walk

    /// <summary>
    /// Builds a compact semantic node for <paramref name="control"/> and recurses over its
    /// merged logical+visual control children, honoring the shared visited-guard and the
    /// total-node budget. Returns <c>null</c> when filtered out, already visited, or the
    /// budget is exhausted.
    /// </summary>
    private static object? BuildSemanticNode(
        ControlRegistry registry,
        IUiAdapter ui,
        object control,
        int remainingDepth,
        bool interactiveOnly,
        HashSet<object> visited,
        NodeBudget budget)
    {
        // Shared visited guard: the merged logical+visual graph is genuinely cyclic in real
        // apps (overlay/adorner cross-links), so this is what keeps the walk from a
        // process-killing StackOverflow and dedups diamond paths.
        if (!visited.Add(control))
            return null;

        if (budget.Exhausted)
            return null;

        var info = ui.GetSemanticInfo(control);

        // When asked for interactive-only we still DESCEND through non-interactive
        // containers (so nested buttons are reached), but we do not EMIT them. A
        // non-interactive node is therefore replaced by its (flattened) interactive
        // descendants rather than dropped wholesale.
        var emit = !interactiveOnly || info.IsInteractive;

        object?[] children;
        if (remainingDepth <= 1)
        {
            children = Array.Empty<object?>();
        }
        else
        {
            var collected = new List<object?>();
            foreach (var child in MergedChildren(ui, control))
            {
                if (budget.Exhausted)
                    break;
                var node = BuildSemanticNode(registry, ui, child, remainingDepth - 1, interactiveOnly, visited, budget);
                if (node is not null)
                    collected.Add(node);
            }
            children = collected.ToArray();
        }

        if (!emit)
        {
            // Non-emitted container: hoist its interactive descendants up a level so the
            // interactive-only view stays a connected tree instead of losing branches.
            return children.Length switch
            {
                0 => null,
                1 => children[0],
                _ => new { hoisted = true, children },
            };
        }

        if (!budget.Take())
            return null;

        return new
        {
            id = registry.Assign(control),
            role = info.Role,
            name = info.Name,
            value = info.Value,
            interactive = info.IsInteractive,
            states = info.States,
            bounds = RectJson(ui.GetBounds(control)),
            children,
        };
    }

    /// <summary>
    /// The merged logical + visual control children of <paramref name="control"/>, in
    /// document order, control-only. Template parts (visual-only) carry real interactive
    /// controls too, so both trees are walked; the shared visited-guard dedups overlap.
    /// </summary>
    private static IEnumerable<object> MergedChildren(IUiAdapter ui, object control)
    {
        foreach (var c in ui.GetLogicalChildren(control).Where(ui.IsControl))
            yield return c;
        foreach (var c in ui.GetVisualChildren(control).Where(ui.IsControl))
            yield return c;
    }

    // ------------------------------------------------------------ mark collection

    /// <summary>
    /// One markable control: its registry handle, semantic info, and on-screen box in the
    /// top-level's client DIP coordinates.
    /// </summary>
    private readonly record struct MarkPick(string Handle, UiSemanticInfo Info, UiRect Rect);

    /// <summary>
    /// Walks <paramref name="topLevel"/> (shared visited-guard, merged logical+visual,
    /// document order) and selects MARKABLE controls: effectively visible AND
    /// (<paramref name="interactiveOnly"/> ? interactive : has a name/role) AND having a
    /// real on-screen box via <see cref="IUiAdapter.TryGetBoundsInTopLevel"/>. Caps at
    /// <paramref name="cap"/> in document order. MUST be called on the UI thread.
    /// </summary>
    private static List<MarkPick> CollectMarkable(
        ControlRegistry registry, IUiAdapter ui, object topLevel, bool interactiveOnly, int cap)
    {
        var picks = new List<MarkPick>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        stack.Push(topLevel);

        // Iterative DFS keeps the document order of a recursive pre-order walk while staying
        // immune to the deep/cyclic graphs that would overflow a recursive walk: we push
        // children in reverse so they pop in document order.
        while (stack.Count > 0 && picks.Count < cap)
        {
            var control = stack.Pop();
            if (!visited.Add(control))
                continue;

            // Only visible controls can carry a usable on-screen box; an invisible subtree
            // also cannot contain visible marks, so prune it entirely.
            if (!ui.IsEffectivelyVisible(control))
                continue;

            var info = ui.GetSemanticInfo(control);
            var markable = interactiveOnly
                ? info.IsInteractive
                : info.IsInteractive || !string.IsNullOrEmpty(info.Name) || !string.IsNullOrEmpty(info.Role);

            if (markable &&
                ui.TryGetBoundsInTopLevel(control, topLevel, out var rect) &&
                rect.Width > 0 && rect.Height > 0)
            {
                picks.Add(new MarkPick(registry.Assign(control), info, rect));
            }

            // Descend (merged logical+visual). Reverse so document order is preserved on pop.
            var children = MergedChildren(ui, control).ToList();
            for (var i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }

        return picks;
    }

    /// <summary>
    /// The shared body of <c>screenshot_marked</c> and <c>describe_screen</c>: collect the
    /// markable controls under <paramref name="topLevel"/>, number them, and render the
    /// annotated PNG via <see cref="IUiAdapter.TryRenderAnnotated"/>. Returns <c>false</c>
    /// with a reason in <paramref name="error"/> when rendering fails. MUST be called on the
    /// UI thread.
    /// </summary>
    private static bool TryRenderMarked(
        ControlRegistry registry,
        IUiAdapter ui,
        McpServerOptions options,
        object topLevel,
        int maxMarks,
        bool interactiveOnly,
        out IReadOnlyList<MarkPick> picks,
        out byte[] png,
        out string error)
    {
        var cap = Math.Clamp(maxMarks <= 0 ? 60 : maxMarks, 1, 500);
        var collected = CollectMarkable(registry, ui, topLevel, interactiveOnly, cap);

        var marks = new List<UiMark>(collected.Count);
        for (var i = 0; i < collected.Count; i++)
            marks.Add(new UiMark(i + 1, collected[i].Rect));

        picks = collected;
        return ui.TryRenderAnnotated(topLevel, options.MaxScreenshotDimension, marks, out png, out error);
    }

    /// <summary>
    /// Builds the mark legend — the array mapping each mark number back to a usable control
    /// handle, role, name, and on-screen box — shared by both set-of-marks tools.
    /// </summary>
    private static object[] BuildLegend(IReadOnlyList<MarkPick> picks) =>
        picks.Select((p, i) => (object)new
        {
            mark = i + 1,
            id = p.Handle,
            role = p.Info.Role,
            name = p.Info.Name,
            bounds = RectJson(p.Rect),
        }).ToArray();

    // --------------------------------------------------------------- small helpers

    /// <summary>A mutable, lambda-capturable total-node budget for the semantic walk.</summary>
    private sealed class NodeBudget
    {
        private int _remaining;
        public NodeBudget(int max) => _remaining = max;

        /// <summary>Whether the budget has run out (no more nodes may be emitted).</summary>
        public bool Exhausted => _remaining <= 0;

        /// <summary>Consumes one node from the budget; returns <c>false</c> when exhausted.</summary>
        public bool Take()
        {
            if (_remaining <= 0)
                return false;
            _remaining--;
            return true;
        }
    }

    private static object RectJson(UiRect b) =>
        new { x = b.X, y = b.Y, width = b.Width, height = b.Height };

    /// <summary>
    /// Resolves the top-level to render/walk: the window containing a resolved
    /// handle/selector target, else the first open top-level. Mirrors
    /// <see cref="ScreenshotTools"/>' resolution so the two behave identically.
    /// </summary>
    private static bool TryResolveTopLevel(
        ControlRegistry registry, IUiAdapter ui, string? target, out object? topLevel, out string error)
    {
        topLevel = null;
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(target))
        {
            // Handle first, then selector (the standard "handle if it resolves, else selector").
            object? control = null;
            if (registry.TryResolve(target!, out var byHandle) && byHandle is not null)
                control = byHandle;
            else
            {
                var matches = registry.Query(target!, ui);
                if (matches.Count > 0)
                    control = matches[0];
            }

            if (control is null)
            {
                error = $"No control found for handle or selector '{target}'.";
                return false;
            }

            topLevel = ui.GetTopLevel(control) ?? control;
            if (topLevel is null)
            {
                error = "The addressed control is not inside any open window.";
                return false;
            }

            return true;
        }

        topLevel = ui.EnumerateRoots().FirstOrDefault();
        if (topLevel is null)
        {
            error = "No open top-level window to capture.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves a control from a handle (preferred) or a selector that must match exactly
    /// one control. MUST be called on the UI thread. Mirrors the shared Resolve pattern in
    /// <see cref="InspectionTools"/>.
    /// </summary>
    private static Resolution Resolve(ControlRegistry registry, IUiAdapter ui, string? handle, string? selector)
    {
        if (!string.IsNullOrWhiteSpace(handle))
        {
            if (registry.TryResolve(handle, out var byHandle) && byHandle is not null)
                return new Resolution { Control = byHandle };
            return new Resolution { Error = Error($"Unknown or collected handle '{handle}'.") };
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            var matches = registry.Query(selector, ui);
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
        public object? Control { get; init; }
        public object? Error { get; init; }
    }

    private static object Error(string message, object? extra = null) =>
        extra is null
            ? new { ok = false, error = message }
            : Merge(new { ok = false, error = message }, extra);

    /// <summary>
    /// Shallow-merges two anonymous objects into a JSON-serializable dictionary so callers
    /// receive a single flat result object.
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
    /// Produces a structured error as a single <see cref="TextContentBlock"/> wrapped in a
    /// <see cref="CallToolResult"/> with <c>IsError</c> set, so the multi-content tools honor
    /// the "never throw on a bad handle/selector" convention.
    /// </summary>
    private static CallToolResult ErrorResult(string message) =>
        new()
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = JsonSerializer.Serialize(new { ok = false, error = message }) },
            },
        };
}
