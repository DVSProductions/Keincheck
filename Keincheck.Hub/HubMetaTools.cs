using System.Text.Json;
using System.Text.Json.Nodes;
using Keincheck.Protocol;
using ModelContextProtocol.Protocol;

namespace Keincheck.Hub;

/// <summary>
/// The hub's <b>meta-tools</b>: the always-present catalog the hub serves itself
/// (independently of any client), plus the helpers that build their schemas, dispatch
/// their calls against an <see cref="IClientBroker"/>, and shape the structured error
/// returned when the AI targets a down/unknown client.
/// </summary>
/// <remarks>
/// These tool <i>names</i> are stable wire surface (the spike + tests assert
/// <c>hub_list_clients</c> / <c>hub_select_client</c>): the <c>hub_</c> prefix keeps
/// them from ever colliding with a client's own tool names (which are proxied
/// verbatim). Everything here is pure: no MCP-session state lives in this type, so the
/// HTTP host and the per-pipe stream sessions share one definition.
/// </remarks>
internal static class HubMetaTools
{
    // ---- canonical names (single source of truth) -------------------------

    public const string ListClients      = "hub_list_clients";
    public const string ListKnownClients = "hub_list_known_clients";
    public const string LaunchClient     = "hub_launch_client";
    public const string RestartClient    = "hub_restart_client";
    public const string SelectClient     = "hub_select_client";
    public const string ClientStatus     = "hub_client_status";
    public const string WaitForClient    = "hub_wait_for_client";
    public const string Guide            = "hub_guide";

    // Record/replay meta-tools. These are listed here (names, schemas, catalog, and
    // IsMetaTool) so they advertise like every other meta-tool, but they are ROUTED in
    // HubMcpServer.HandleCallToolAsync BEFORE this type's DispatchAsync, because they need
    // the recorder/broker state that lives on the server (not in this pure helper).
    public const string RecordStart  = "hub_record_start";
    public const string RecordStop   = "hub_record_stop";
    public const string RecordStatus = "hub_record_status";
    public const string Replay       = "hub_replay";
    public const string ExportTest   = "hub_export_test";

    /// <summary>True if <paramref name="name"/> is one of the hub's own meta-tools.</summary>
    public static bool IsMetaTool(string name) => name switch
    {
        ListClients or ListKnownClients or LaunchClient or RestartClient
            or SelectClient or ClientStatus or WaitForClient or Guide
            or RecordStart or RecordStop or RecordStatus or Replay or ExportTest => true,
        _ => false,
    };

    /// <summary>
    /// The optional <c>client</c> argument every <i>proxied</i> UI tool accepts to
    /// override the active client for a single call. Surfaced in this module so the
    /// proxy and the docs stay in lock-step.
    /// </summary>
    public const string ClientOverrideArg = "client";

    // ---- catalog ----------------------------------------------------------

    /// <summary>Builds the always-present meta-tool entries (order is stable).</summary>
    public static IEnumerable<Tool> BuildCatalog()
    {
        // Always first: the onboarding guide, so a fresh AI can read the broker workflow
        // before touching anything else.
        yield return Meta(Guide,
            "Read this first. Returns a markdown onboarding guide to the Keincheck broker: "
            + "the AI<->hub<->apps model, the discovery->select->drive flow, the meta-tool "
            + "catalog, the typical UI tools, the selector grammar, set-of-marks, "
            + "record/replay, and gotchas. No arguments.");

        yield return Meta(ListClients,
            "List the Keincheck clients currently connected to the hub "
            + "(id, app id, display name, pid, read-only, tool count). No arguments.");

        yield return Meta(ListKnownClients,
            "List every client the hub has seen this run, including disconnected ones "
            + "(for launch/restart). No arguments.");

        yield return Meta(LaunchClient,
            "Launch a known client by id (uses its recorded executable path). The app "
            + "connects back on its own. Args: { \"clientId\": string }.",
            ClientIdSchema());

        yield return Meta(RestartClient,
            "Restart a client: terminate the running instance (if any) and launch it "
            + "again. Use this when a client has dropped. Args: { \"clientId\": string }.",
            ClientIdSchema());

        yield return Meta(SelectClient,
            "Make a client active so its tools are advertised (emits "
            + "tools/list_changed). Args: { \"clientId\": string }.",
            ClientIdSchema());

        yield return Meta(ClientStatus,
            "Report the full status of one client (connected, read-only, pid, tools, "
            + "last-seen). Args: { \"clientId\": string }.",
            ClientIdSchema());

        yield return Meta(WaitForClient,
            "Block until a client connects, then return it. Use after launching/rebuilding "
            + "an app to wait for it to come back (its tools re-list automatically). Match "
            + "by appId or clientId; omit both to wait for any client. "
            + "Args: { \"appId\"?: string, \"clientId\"?: string, \"timeoutMs\"?: int "
            + "(default 30000) }. Returns { clientId, connected:true } or a timeout result.",
            WaitForClientSchema());

        // ---- record / replay / export ----
        // (Routed in HubMcpServer before DispatchAsync; advertised here.)

        yield return Meta(RecordStart,
            "Start recording proxied UI tool calls. Clears the buffer and captures every "
            + "subsequent (non-meta) tool call until stopped. Args: { \"name\"?: string }.",
            RecordStartSchema(), readOnly: false);

        yield return Meta(RecordStop,
            "Stop the active recording (the buffer is kept for replay/export). Returns the "
            + "captured step count. No arguments.",
            readOnly: false);

        yield return Meta(RecordStatus,
            "Report whether a recording is active, its name, and how many steps are "
            + "buffered. No arguments.");

        yield return Meta(Replay,
            "Re-issue every buffered step to its original client, in order. Args: "
            + "{ \"stopOnError\"?: bool (default false), \"delayMs\"?: int (default 0) }. "
            + "Steps whose client is no longer connected are skipped.",
            ReplaySchema(), readOnly: false);

        yield return Meta(ExportTest,
            "Export the current recording as a reusable artifact. format=\"json\" yields a "
            + "replayable scenario document; format=\"csharp\" yields a best-effort xUnit "
            + "[Fact] skeleton (a starting point, not guaranteed to compile). "
            + "Args: { \"format\"?: \"json\" | \"csharp\" (default \"json\") }.",
            ExportTestSchema());
    }

    // ---- dispatch ---------------------------------------------------------

    /// <summary>
    /// Handles a meta-tool call synchronously against the broker. Caller has already
    /// confirmed <see cref="IsMetaTool"/>. Returns the MCP result to relay to the AI.
    /// </summary>
    public static async ValueTask<CallToolResult> DispatchAsync(
        IClientBroker broker, string name, JsonElement? args,
        Action onActiveChanged, CancellationToken ct)
    {
        switch (name)
        {
            case Guide:
                // Pure: a static onboarding document, no broker state needed.
                return new CallToolResult
                {
                    Content = new List<ContentBlock> { new TextContentBlock { Text = GuideMarkdown } },
                };

            case ListClients:
            {
                var live = LiveIds(broker);
                return JsonResult(broker.ListClients().Select(c => ToView(c, live)));
            }

            case ListKnownClients:
            {
                var live = LiveIds(broker);
                return JsonResult(broker.ListKnownClients().Select(c => ToView(c, live)));
            }

            case SelectClient:
            {
                if (!TryGetClientId(args, out var id, out var err))
                    return ErrorResult(err);
                if (broker.ClientStatus(id) is null)
                    return DownClientError(id, "is not known to the hub");
                broker.ActiveClientId = id;
                onActiveChanged();
                return JsonResult(new { activeClientId = id });
            }

            case ClientStatus:
            {
                if (!TryGetClientId(args, out var id, out var err))
                    return ErrorResult(err);
                var info = broker.ClientStatus(id);
                return info is null
                    ? DownClientError(id, "is not known to the hub")
                    : JsonResult(ToView(info, LiveIds(broker)));
            }

            case WaitForClient:
            {
                // Both filters are optional; a clientId wins over appId, and neither set
                // means "wait for any client". timeoutMs defaults to 30s.
                var filter = TryGetStringProp(args, "clientId") ?? TryGetStringProp(args, "appId");
                var timeoutMs = TryGetIntProp(args, "timeoutMs") ?? 30000;
                var timeout = TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs));

                var info = await broker.WaitForClientAsync(filter, timeout, ct).ConfigureAwait(false);
                return info is null
                    ? JsonResult(new
                    {
                        connected = false,
                        timedOut = true,
                        waitedFor = filter,
                        timeoutMs,
                    })
                    : JsonResult(new { clientId = info.ClientId, connected = true });
            }

            case LaunchClient:
            {
                if (!TryGetClientId(args, out var id, out var err))
                    return ErrorResult(err);
                try
                {
                    var pid = await broker.LaunchClientAsync(id, ct).ConfigureAwait(false);
                    return JsonResult(new { launched = id, processId = pid });
                }
                catch (Exception ex)
                {
                    return ErrorResult($"Failed to launch '{id}': {ex.Message}");
                }
            }

            case RestartClient:
            {
                if (!TryGetClientId(args, out var id, out var err))
                    return ErrorResult(err);
                try
                {
                    var pid = await broker.RestartClientAsync(id, ct).ConfigureAwait(false);
                    return JsonResult(new { restarted = id, processId = pid });
                }
                catch (Exception ex)
                {
                    return ErrorResult($"Failed to restart '{id}': {ex.Message}");
                }
            }

            default:
                // Unreachable: callers gate on IsMetaTool. Defensive only.
                return ErrorResult($"Unknown meta-tool '{name}'.");
        }
    }

    // ---- onboarding guide -------------------------------------------------

    /// <summary>
    /// The markdown returned by <c>hub_guide</c>. A single static document describing the
    /// broker model and the discovery → select → drive workflow for a fresh AI session.
    /// </summary>
    private const string GuideMarkdown = """
# Keincheck Hub — broker guide

You are talking to a **broker**, not to one app. The hub is a generic multiplexer.

```
   AI  <--- MCP (this connection) --->  HUB  <--- named pipe --->  app#1, app#2, ...
```

- The **hub** speaks MCP to you and a named pipe to each connected Keincheck-enabled app.
- UI tools (`list_windows`, `query_controls`, ...) actually run *inside* the target app's
  process. The hub just forwards `tools/call` to the owning app and relays the result.
- The hub's own **meta-tools** are prefixed `hub_` so they never collide with an app's tools.

## The flow: discover → select → drive

1. **Discover.** `hub_list_clients` — the apps connected right now (id, app id, display
   name, pid, read-only, **ownsWindows**, tool count). `hub_list_known_clients` also lists
   ones that have disconnected (so you can launch/restart them). When one app launch shows
   several clients, the one with `ownsWindows: true` is the UI-owning process — pick it.
2. **Select.** `hub_select_client` with `{ "clientId": "app#1" }` makes one app *active*.
   Its tools are then advertised to you (the hub emits `tools/list_changed`). Until you
   select, only the meta-tools exist.
3. **Drive.** Call the app's UI tools directly. To target a *different* app for a single
   call without changing the active selection, pass a `"client"` argument, e.g.
   `query_controls({ "client": "app#2", "selector": "Button" })`.

After a rebuild: kill the app, change code, relaunch. The reconnecting client **keeps its
id and re-becomes active automatically** — its tools re-list with no `hub_select_client`.
To block until it is back, call `hub_wait_for_client { "appId": "myapp" }`.

## Meta-tool catalog

- `hub_guide` — this document.
- `hub_list_clients` / `hub_list_known_clients` — connected / ever-seen clients (each entry
  carries `ownsWindows` so you can spot the UI-owning process).
- `hub_select_client { clientId }` — set the active client.
- `hub_client_status { clientId }` — full status of one client.
- `hub_wait_for_client { appId?, clientId?, timeoutMs? }` — block until a (matching) client
  connects, then return it. Use after launching/rebuilding to wait for the app to come back.
- `hub_launch_client { clientId }` / `hub_restart_client { clientId }` — start / restart a
  known app by its recorded executable path. A restarted client keeps the **same id**.
- `hub_record_start { name? }` / `hub_record_stop` / `hub_record_status` — record the
  proxied UI tool calls you make.
- `hub_replay { stopOnError?, delayMs? }` — re-issue the recorded steps.
- `hub_export_test { format? }` — export the recording as `json` or a `csharp` xUnit skeleton.

## Typical UI tools (provided by the active app)

- `list_windows` — top-level windows.
- `query_controls` — find controls by selector (returns handles + a summary).
- `get_semantic_tree` — a compact, AI-friendly tree of the meaningful controls.
- `get_properties` — read properties of a control handle.
- `screenshot_marked` — a screenshot with **set-of-marks**: numbered boxes over the
  interactive controls, plus a legend mapping each number to a handle/selector. Pick a
  number, then act on that handle.
- `automation_action` — invoke a control's accessibility action (click, toggle, expand…).
- `click_at` / `type_text` / `send_keys` — raw pointer/keyboard input by point or to focus.
- `wait_for_idle` — block until the UI settles (use after an action before asserting).

## Selector grammar (CSS-ish)

- Type: `Button`, `TextBox` — match by control type (matches subtypes too).
- Name/id: `#submit` — match by name/automation id.
- Class: `.primary` — match an author-declared style class (Avalonia `Classes="primary"`).
  Combine with a type (`Button.primary`) or require several (`.a.b`). Frameworks without
  style classes (e.g. WPF) match nothing here.
- Attribute: `[Text=Save]` — match by a property value; composes with the above
  (`Button.primary[IsDefault=true]`).
- Descendant: `Window TextBox` — a `TextBox` anywhere under a `Window`.
Combine them: `#dialog Button.primary`.

## Set-of-marks workflow

`screenshot_marked` → read the numbered legend → choose the control you want → act on its
handle/selector with `automation_action` or `click_at`. This avoids guessing pixel
coordinates and is robust to layout shifts.

## Record / replay

1. `hub_record_start { "name": "login-flow" }`.
2. Drive the UI normally — every proxied (non-meta) tool call is captured with its client,
   args, and ok/fail.
3. `hub_record_stop` → returns the step count.
4. `hub_replay { "stopOnError": true, "delayMs": 200 }` to re-run it, or
   `hub_export_test { "format": "json" }` / `{ "format": "csharp" }` to save it.

## Gotchas

- **No active client?** UI tools return a structured error telling you to
  `hub_select_client` first (or pass a `"client"` arg).
- **A client dropped?** Calls to it return a `client_unavailable` error naming
  `hub_restart_client`. Restarting keeps the **same id**, so a recording still replays. If
  the app was the active one and reconnects on its own (a rebuild loop), it re-becomes
  active automatically — no need to re-select. Use `hub_wait_for_client` to block for it.
- **Blank screenshots?** A **locked workstation** renders nothing — screenshots come back
  blank. Unlock the session (or expect empty captures) before relying on vision.
- **Read-only clients** refuse mutating tools; `hub_client_status` shows the flag.
""";

    // ---- structured errors ------------------------------------------------

    /// <summary>
    /// The structured error returned when the AI calls a tool against a client that is
    /// down or unknown. It is a <see cref="CallToolResult"/> with <c>IsError</c> set and
    /// a machine-readable <c>structuredContent</c> object that names the recovery tool
    /// (<see cref="RestartClient"/>) so the model can self-heal.
    /// </summary>
    public static CallToolResult DownClientError(string clientId, string reason)
    {
        var text =
            $"Client '{clientId}' {reason}. It cannot service tool calls right now. "
            + $"Call {RestartClient} with {{ \"clientId\": \"{clientId}\" }} to bring it "
            + $"back, or {SelectClient} a different one ({ListClients} shows live clients).";

        var structured = new JsonObject
        {
            ["error"] = "client_unavailable",
            ["clientId"] = clientId,
            ["reason"] = reason,
            ["recovery"] = new JsonObject
            {
                ["tool"] = RestartClient,
                ["arguments"] = new JsonObject { ["clientId"] = clientId },
            },
        };

        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(structured),
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
        };
    }

    // ---- shared result helpers (reused by the proxy in HubMcpServer) -------

    public static CallToolResult JsonResult(object? value)
    {
        var json = JsonSerializer.Serialize(value, ProtocolJson.Options);
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
        };
    }

    public static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
    };

    // ---- internals --------------------------------------------------------

    /// <summary>
    /// A trimmed, serialization-friendly projection of a client snapshot.
    /// <paramref name="liveIds"/> is the set of currently-connected hub-ids; the
    /// projected <c>connected</c> is recomputed from live membership rather than the
    /// stored flag so it can never drift from reality (finding-3 hardening).
    /// </summary>
    private static object ToView(ClientInfo c, IReadOnlySet<string> liveIds) => new
    {
        clientId = c.ClientId,
        appId = c.AppId,
        displayName = c.DisplayName,
        processId = c.ProcessId,
        connected = liveIds.Contains(c.ClientId),
        ownsWindows = c.OwnsWindows,
        readOnly = c.ReadOnly,
        toolCount = c.Tools.Count,
        executablePath = c.ExecutablePath,
        lastSeenUtc = c.LastSeenUtc,
    };

    /// <summary>The set of currently-connected hub-ids, the live-membership source of truth for <c>connected</c>.</summary>
    private static IReadOnlySet<string> LiveIds(IClientBroker broker) =>
        broker.ListClients().Select(c => c.ClientId).ToHashSet(StringComparer.Ordinal);

    private static bool TryGetClientId(JsonElement? args, out string clientId, out string error)
    {
        clientId = "";
        error = "";
        if (args is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty("clientId", out var v)
            && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } id)
        {
            clientId = id;
            return true;
        }
        error = "Missing required argument: 'clientId' (a non-empty string).";
        return false;
    }

    /// <summary>Reads a non-empty string property from a JSON object arg, or null.</summary>
    private static string? TryGetStringProp(JsonElement? args, string prop) =>
        args is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
        && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    /// <summary>Reads an int property from a JSON object arg, or null.</summary>
    private static int? TryGetIntProp(JsonElement? args, string prop) =>
        args is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    private static Tool Meta(string name, string description, JsonElement? schema = null, bool readOnly = true) => new()
    {
        Name = name,
        Description = description,
        InputSchema = schema ?? EmptyObjectSchema(),
        // Most meta-tools never mutate app state; the record/replay ones do, so they pass
        // readOnly:false and are not hinted as read-only.
        Annotations = new ToolAnnotations { ReadOnlyHint = readOnly },
    };

    public static JsonElement EmptyObjectSchema() =>
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    public static JsonElement ClientIdSchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"clientId":{"type":"string","description":"The hub-assigned client id."}},"required":["clientId"]}""")
            .RootElement.Clone();

    public static JsonElement WaitForClientSchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"appId":{"type":"string","description":"The app's self-reported id to wait for (matches any instance of that app)."},"clientId":{"type":"string","description":"A specific hub-assigned client id to wait for (wins over appId)."},"timeoutMs":{"type":"integer","description":"Max milliseconds to wait before giving up (default 30000)."}}}""")
            .RootElement.Clone();

    public static JsonElement RecordStartSchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"name":{"type":"string","description":"Optional label for the recording."}}}""")
            .RootElement.Clone();

    public static JsonElement ReplaySchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"stopOnError":{"type":"boolean","description":"Stop replaying at the first failing step (default false)."},"delayMs":{"type":"integer","description":"Milliseconds to wait between steps (default 0)."}}}""")
            .RootElement.Clone();

    public static JsonElement ExportTestSchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"format":{"type":"string","enum":["json","csharp"],"description":"Output format: a replayable JSON scenario, or an xUnit [Fact] skeleton."}}}""")
            .RootElement.Clone();
}
