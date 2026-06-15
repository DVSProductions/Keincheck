# Keincheck — User Review (2026-06-15)

**Reviewer:** AI agent (Claude, via Claude Code MCP) driving a real task.
**Scenario:** Inspect and optimize the UI of the *Animation Editor* window in `ProtoFaceAvalonia`
(Avalonia 12 / .NET 10), connected through the `keincheck-hub` MCP server (`keincheck-connect.exe`
stdio bridge). Found a toolbar-overflow bug and a layers-panel spacing issue, fixed both in XAML,
and verified the result — entirely through Keincheck plus one external fallback.

**Verdict: 9/10.** This is genuinely the difference between "guess at XAML and hope" and a real
inspect → hypothesize → tweak-live → verify loop. The live property editing and binding-error check
are standout features. The main rough edge is connection lifecycle during a rebuild loop.

---

## What worked extremely well

- **`hub_guide` onboarding.** The broker mental model (AI ⇄ MCP ⇄ hub ⇄ apps) and the
  discover → select → drive flow were clear enough to be productive on the first call. The selector
  grammar and set-of-marks sections were enough to start without trial-and-error.
- **`set_property` for LIVE layout editing — the killer feature.** I trimmed `Slider.Width`
  (96→78, 90→74, 84→74) on the *running* app and screenshotted to see whether a toolbar wrap would
  resolve, *before* baking anything into XAML. That turned a speculative edit-rebuild-check cycle
  into an instant experiment. This alone justifies the tool.
- **`get_binding_errors`.** Instant, definitive "0 errors" after each change. For XAML work this is
  the single most reassuring signal — please keep it prominent.
- **`screenshot_window` on GPU-composited Avalonia.** Captured the editor cleanly every time. For
  contrast, my external fallback (Win32 `PrintWindow`) returned *blank* frames until I passed
  `PW_RENDERFULLCONTENT` (flag 2) — Keincheck "just worked," which is a real selling point for
  Avalonia/Skia apps where naive capture fails.
- **`get_semantic_tree` + `query_controls`.** Found the "Animation Editor" button by Name and all
  four `Slider`s by type immediately. Bounds came back in **logical px** (matched the 1100×720
  window exactly), so reasoning about layout was DPI-independent — excellent.
- **`automation_action` (Invoke / Select).** Drove the button and selected a ListBoxItem with no
  fuss; the returned `state` (`isSelected=True`) was a nice confirmation.

## Friction & suspected bugs

1. **Rebuild kills the connection (biggest pain).** To recompile I had to `Stop-Process` the app,
   which dropped the MCP server — all 27 tool schemas got delisted on the host side and had to be
   re-fetched after relaunch. In a UI-fix loop (inspect → edit XAML → rebuild → re-inspect) this
   happens every iteration. There *is* a `Keincheck.Hub` project, so the broker is meant to be
   standalone — but empirically, killing the only app instance took the MCP bridge down with it.
   Worth confirming whether `keincheck-connect.exe`/the hub exits when its last client disconnects,
   and if so, keeping a persistent broker alive across client restarts. A
   `hub_rebuild_client { csproj }` or "wait for client to reconnect" affordance would make the inner
   loop seamless.

2. **One launch → two connected clients.** Starting a single `ProtoFaceAvalonia.exe` produced
   *two* connected clients (`protoface#2` and `protoface#3`); a child/worker process appears to
   register too. I had to call `list_windows` on each to find the one that actually owns the editor
   window. Suggestion: flag which client owns top-level windows, dedupe by window-owning PID, or let
   non-UI child processes opt out of registration.

3. **Stale connection state in `hub_list_known_clients`.** On first contact, `hub_list_clients`
   returned `[]` while `hub_list_known_clients` listed a client with `connected: true` and a recent
   `lastSeenUtc`. The `connected` flag didn't reflect reality. Minor, but it made discovery
   ambiguous ("is it connected or not?").

4. **`.class` selector didn't match Avalonia style classes.** `query_controls(".toolGroup")`
   returned 0 even though the control is declared `Classes="toolGroup"`. The selector docs say
   `.primary` matches "a class/style tag the app exposes," so I expected Avalonia `StyleClasses` to
   match. Either wire `.class` to `StyleClass` membership for Avalonia, or clarify in the grammar
   what `.class` actually resolves to.

5. **Selector/handle ergonomics.** `set_property` and `automation_action` made me pass the unused
   discriminator explicitly (`handle: null` when using `selector`, and `selector: null` when using
   `handle`). Making the unused one truly optional would tidy call sites.

## Nice-to-haves

- **Survive-rebuild story** (see #1) — the highest-leverage improvement for agent-driven dev loops.
- **Semantic-tree pruning.** `get_semantic_tree` included a lot of template-internal chrome
  (`Track`/`Thumb` parts, `DataValidationErrors`, nested `ContentPresenter`s) as `interactive:false`
  noise. A stronger "meaningful controls only" mode (or default) would cut the payload substantially.
- **`set_property` persistence hint.** It would help to know in the result whether a set value is
  transient (lost on next layout pass / style re-application) vs. sticky, so an agent knows it's
  experimenting vs. mutating.
- **A diff between two semantic-tree / screenshot snapshots** ("what changed after this action")
  would be powerful for verifying an edit did exactly what was intended and nothing else.

## Resolution (v0.5.0)

Each finding was re-verified against the source before acting. Shipped in **v0.5.0**:

- **#1 rebuild loop** — the "hub dies with the app" hypothesis was *false* (the hub is a standalone tray daemon; the 27 tools delist by design when the active client drops). Root cause was a missing **auto-reselect**: the broker never cleared `_active`, so a relaunched app's tools stayed delisted until a manual `hub_select_client`. Fixed — a reconnecting previously-active client now reclaims `active` automatically (a deliberate manual selection of a different client is respected). Added a **`hub_wait_for_client`** meta-tool to block until an app (re)connects.
- **#2 duplicate clients** — confirmed bug: the hub minted a fresh suffix per `Register` with no PID dedup, and the client auto-reconnects. Fixed — a re-`Register` from the same `AppId`+PID reuses the same hub-id (stale session evicted). Added an **`ownsWindows`** flag to the client list so the AI can pick the UI-owner without probing each. (ProtoFace itself was clean — single `UseMcpClient`.)
- **#3 stale `connected`** — already forced false in code; hardened the projection to recompute `connected` from live membership so it can never drift.
- **#4 `.class` selector** — confirmed gap: it was advertised in the hub guide but unimplemented. Implemented over Avalonia `StyleClasses` (via a defaulted `IUiAdapter.GetClasses`; `.toolGroup`, `Type.class`, `.a.b`, `.class[Attr=v]`). Reconciled the guide — `.class` is now real; the also-unimplemented `:contains`/`:nth` claims were removed.
- **#5 selector/handle ergonomics** — confirmed bug: `handle`/`selector` lacked defaults so MCP marked them required. Reordered so `set_property`/`automation_action`/`set_focus` no longer force the unused discriminator.
- **Semantic-tree pruning** — added `meaningfulOnly` (default true) to `get_semantic_tree`, dropping anonymous template chrome (`Track`/`Thumb`/`ContentPresenter`/…) while keeping real and named controls.

Deferred to a later release: the `set_property` persistence/precedence hint and the snapshot-diff tool (design-heavy). 162 tests pass.

## Bottom line

For a from-cold task — connect, find a real layout bug, test a fix live, bake it, and verify with
zero binding errors — Keincheck made the whole loop *fast and grounded in the running app* instead
of guesswork. Fix the rebuild-disconnect lifecycle and the duplicate-client confusion and this is a
best-in-class tool for AI-driven UI work on Avalonia.
