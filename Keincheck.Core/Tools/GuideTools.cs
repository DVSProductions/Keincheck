using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Keincheck.Core.Tools;

/// <summary>
/// A single self-contained onboarding tool. When Keincheck is embedded in a host app, the
/// model that connects has no out-of-band documentation; <c>keincheck_guide</c> hands it a
/// thorough Markdown briefing — what Keincheck is, the canonical inspect → orient → act →
/// settle workflow, the selector grammar, the set-of-marks / semantic-tree usage, and the
/// known gotchas — so it can drive the UI competently from the very first call.
/// </summary>
[McpServerToolType]
public static class GuideTools
{
    /// <summary>
    /// Returns the Keincheck onboarding guide as Markdown. Takes no arguments.
    /// </summary>
    [McpServerTool(Name = "keincheck_guide"),
     Description("Read this FIRST. Returns a Markdown guide to Keincheck: what it is, the inspect->orient->act->settle workflow, the CSS-ish selector grammar, set-of-marks + semantic-tree usage, and gotchas. No arguments.")]
    public static Task<object> KeincheckGuide() =>
        Task.FromResult<object>(new { ok = true, guide = Guide });

    // Kept as a single verbatim string so the briefing ships with the package and stays in
    // lockstep with the tool surface it documents.
    private const string Guide =
"""
# Keincheck — driving a live desktop UI

Keincheck is an MCP toolset embedded **inside** a running desktop application (Avalonia
or WPF). It lets you *see* and *drive* that app's real, live UI: enumerate windows, walk
the control tree, read/write properties, take screenshots (including annotated
"set-of-marks" shots), and synthesize input. Every tool runs in-process against the
genuine visual tree — there is no separate browser or DOM.

Each control you discover gets a stable **handle** (e.g. `ctl-1a`). Pass that handle (or a
CSS-ish **selector**) to subsequent tools. Handles are weak: a closed/collected control's
handle stops resolving and tools report `{ ok: false, error: ... }` rather than throwing.

## The workflow

1. **Inspect — find out what's open.**
   - `list_windows` — every open top-level (window / popup / single-view root) with its
     handle, title, type, bounds, and active state.
   - `query_controls` — resolve a selector to matching controls.
   - `get_semantic_tree` — the **accessibility** view (roles / names / values / states),
     usually more useful than the raw type tree. `get_logical_tree` / `get_visual_tree`
     give the raw logical/visual trees when you need template parts or exact structure.

2. **Orient — see the screen.**
   - `describe_screen` — the **one-call** orientation tool. Returns a *set-of-marks*
     screenshot (interactive controls drawn as numbered boxes) **plus** a JSON legend
     mapping each number to a real control handle **plus** a shallow semantic summary.
     Start here when you don't yet know the layout.
   - `screenshot_marked` — just the annotated screenshot + legend.
   - `screenshot_window` / `screenshot_control` — plain (un-annotated) PNGs.

3. **Act — change something.** Prefer semantic automation; fall back to synthetic input.
   - `automation_action` — invoke / toggle / set-value / expand / collapse / select via the
     control's UI-Automation peer (the robust, accessibility-driven path).
   - `set_property` — set a property directly (with type coercion, e.g. `"10,5,10,5"`).
   - `set_focus`, `type_text`, `send_keys` — focus a control, type literal text, or send
     key chords (`"Ctrl+S"`, `"Down Down Enter"`).
   - `click_at` / `pointer` / `scroll_at` — synthetic pointer input at window coordinates,
     for **custom-drawn controls that expose no automation peer**. Coordinates are inside
     the target top-level (the same DIP space the `bounds`/legend report).

4. **Settle — let the UI catch up.**
   - `wait_for_idle` — block until the UI thread drains pending layout/render, so your next
     inspection sees a *settled* tree. Call it after acting.
   - `wait_for` — poll until a selector matches OR a property equals a value OR timeout.

## Selector grammar (CSS-ish)

Whitespace-separated combinator chain, evaluated over the merged logical+visual tree.
Name matching is **ordinal / case-sensitive**. A malformed or empty selector matches
nothing (it never throws).

- `Type` — matches by control type name (e.g. `Button`). Matches the exact runtime type
  name **or any base type name** (so `Button` also matches a `ToggleButton`, `Control`
  matches any control).
- `.class` — matches by **author style-class** membership (e.g. `.toolGroup` matches a
  control declared `Classes="toolGroup"`). Ordinal / case-sensitive. Combine with a type
  (`Button.primary`), chain classes to require all of them (`.a.b`), or add attributes
  (`.primary[Name=Save]`). Frameworks without style classes (e.g. WPF) expose none, so
  `.class` matches nothing there.
- `Type[Name=x]` — a type plus a `Name` attribute equal to `x`.
- `#Name` — any control whose `Name` equals `Name`.
- `A B` — descendant combinator: `B` anywhere under `A`.
- `A > B` — child combinator: `B` that is a *direct* child of `A`.

Attribute predicates also accept `[Name=x]` standalone and quoting:
`[Name='my value']` or `[Name="my value"]`.

Examples: `Button[Name=Save]`, `#submit`, `.toolGroup`, `Button.primary`,
`StackPanel > TextBox`, `Window TextBox`.

Many tools take **either** `handle` **or** `selector`; when both are accepted the handle
wins. For tools that need exactly one control (e.g. `get_properties`, `set_property`), a
selector matching several controls is an error that lists the candidate handles — narrow it.

## Set-of-marks + semantic tree (how to act efficiently)

The fast loop for an unfamiliar screen:

1. Call `describe_screen`. Look at the numbered screenshot and read the legend
   (`[{ mark, id, role, name, bounds }]`).
2. Pick the control you want by its mark number, then use its `id` (handle) with
   `automation_action` / `type_text` / `click_at` — **do not** re-derive coordinates if you
   have a handle; the handle is more robust.
3. For controls with no automation peer (custom-drawn), use the legend `bounds` with
   `click_at` (the bounds are already in the window's coordinate space).
4. After acting, `wait_for_idle`, then re-`describe_screen` or `get_semantic_tree` to
   confirm the result.

`get_semantic_tree` with `interactiveOnly: true` is the cheapest way to list everything
actionable without an image; nodes carry `{ id, role, name, value, interactive, states,
bounds }`.

## Gotchas

- **Locked workstation → blank screenshots.** When the Windows session is locked (or the
  desktop is otherwise not composited), the OS hands back an empty surface and screenshots
  come out blank/black. Inspection and automation still work; only rendering is affected.
- **Everything is on the UI thread.** Every tool marshals onto the app's UI thread, so a
  hung or modal-blocked UI thread will make tools slow or time out. After driving input,
  `wait_for_idle` instead of guessing a sleep.
- **Handles are weak.** A handle to a control that has since been closed/collected stops
  resolving — re-query rather than reusing a stale handle across big UI changes.
- **Tree dumps are capped.** Depth- and total-node caps keep payloads bounded; a
  `truncated: true` in the result means there was more — narrow the root (handle/selector)
  or lower the depth rather than fighting the cap.
- **Errors are data, not exceptions.** A bad handle/selector returns
  `{ ok: false, error: "..." }` (image tools return that JSON as a text content block with
  `isError` set). Check `ok` before trusting a result.
- **Synthetic input vs. automation.** Prefer `automation_action` (semantic, robust). Reach
  for `click_at` / `pointer` / `type_text` only for controls without an automation peer.
""";
}
