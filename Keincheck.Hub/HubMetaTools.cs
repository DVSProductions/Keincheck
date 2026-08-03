using System.Reflection;
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
    public const string Status           = "hub_status";
    public const string Guide            = "hub_guide";

    // Lifting read-only is available here for the same reason issuance is: the tray checkbox
    // and this tool carry identical authorization (runs as this user), so gating one and not
    // the other is friction, not a boundary. It was tray-only at first, which meant driving a
    // remote machine from an AI session required alt-tabbing to a GUI to permit it.
    public const string SetReadOnly      = "hub_set_readonly";

    // Remote access. These administer the listener; they do NOT drive clients — a remote
    // client is driven through exactly the same tools as a local one, which is the whole
    // point.
    //
    // Issuance is available here, on the same footing as everything else. The MCP endpoint
    // and the control pipe carry the SAME authorization — both are reachable by anything
    // running as this user, and nothing else — and the pipe already issues credentials
    // without a prompt, because that is what the build-time enrollment step uses. Gating the
    // MCP path while leaving the pipe path open would not have protected anything: a caller
    // that wanted a credential could simply use the pipe. It would only have been friction.
    public const string RemoteStatus  = "hub_remote_status";
    public const string RemoteEnable  = "hub_remote_enable";
    public const string RemoteDisable = "hub_remote_disable";
    public const string RemoteIssue   = "hub_remote_issue";
    public const string RemoteRevoke  = "hub_remote_revoke";

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
            or SelectClient or ClientStatus or WaitForClient or Status or Guide
            or RecordStart or RecordStop or RecordStatus or Replay or ExportTest
            or RemoteStatus or RemoteEnable or RemoteDisable or RemoteIssue or RemoteRevoke
            or SetReadOnly => true,
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

        yield return Meta(Status,
            "Report the hub's own version, protocol version, active client, and connected "
            + "client count. No arguments.");

        yield return Meta(SetReadOnly,
            "Allow or forbid mutating tools (click_at, type_text, set_property, ...) for one "
            + "client. Remote clients START read-only, so call this with readOnly=false before "
            + "trying to drive one. The decision is remembered per machine and survives "
            + "reconnects and hub restarts. "
            + "Args: { \"clientId\": string, \"readOnly\": bool }.",
            SetReadOnlySchema(), readOnly: false);

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

        // ---- remote access ----

        yield return Meta(RemoteStatus,
            "Report whether this hub accepts clients from other machines: enabled, the bound "
            + "address, and the issued credentials (host, serial, expiry, revoked). Remote "
            + "clients appear in hub_list_clients as \"appId@host#n\" with host and transport "
            + "set, and are driven with the SAME tools as local ones. No arguments.");

        yield return Meta(RemoteEnable,
            "Start accepting clients from other machines, provisioning this hub's certificate "
            + "authority on first use. Args: { \"bindAddress\"?: string (default "
            + "\"127.0.0.1\"), \"port\"?: int, \"advertisedEndpoint\"?: string }. This only "
            + "opens the listener -- a client also needs a credential, from hub_remote_issue.",
            RemoteEnableSchema(), readOnly: false);

        yield return Meta(RemoteIssue,
            "Issue a credential a client on another machine uses to attach, provisioning this "
            + "hub's certificate authority if needed. Args: { \"target\": string (the machine "
            + "label, which becomes the '@host' in that client's id), \"days\"?: int, "
            + "\"note\"?: string, \"outPath\"?: string (also write it to a file) }. Returns the "
            + "credential bundle: set it as KEINCHECK_REMOTE on the target machine, or point "
            + "KEINCHECK_REMOTE_FILE at the file. Revoke it with hub_remote_revoke.",
            RemoteIssueSchema(), readOnly: false);

        yield return Meta(RemoteDisable,
            "Stop accepting clients from other machines. Already-connected remote clients stay "
            + "until they disconnect. The certificate authority and issued-credential list are "
            + "kept, so re-enabling does not invalidate existing credentials. No arguments.",
            readOnly: false);

        yield return Meta(RemoteRevoke,
            "Permanently refuse a remote credential by serial (hub_remote_status lists them). "
            + "Takes effect on that client's next connection attempt. Use this if a credential "
            + "leaks -- one baked into a shipped build is extractable from that build. "
            + "Args: { \"serial\": string }.",
            RemoteRevokeSchema(), readOnly: false);
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

            case Status:
            {
                // Pure metadata about the hub itself — no client targeted. Lets an operator
                // see which hub/protocol build is running and how many apps are connected.
                return JsonResult(new
                {
                    hubVersion = HubAssemblyVersion,
                    protocolVersion = ProtocolVersion.Current,
                    protocolRange = new { minimum = ProtocolVersion.Minimum, current = ProtocolVersion.Current },
                    activeClientId = broker.ActiveClientId,
                    clientCount = broker.ListClients().Count,
                });
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

            case RemoteStatus:
            {
                var remote = HubRuntime.Remote;
                if (remote is null)
                    return JsonResult(new { available = false, reason = "This hub build has no remote support." });

                var settings = remote.Store.Settings;
                return JsonResult(new
                {
                    available = true,
                    enabled = settings.Enabled,
                    listening = remote.IsListening,
                    boundEndpoint = remote.BoundEndpoint,
                    bindAddress = settings.BindAddress,
                    port = settings.Port,
                    advertisedEndpoint = settings.AdvertisedEndpoint,
                    provisioned = remote.Store.IsProvisioned,
                    credentials = remote.Store.Issued().Select(c => new
                    {
                        c.Serial, c.Host, c.IssuedUtc, c.NotAfter, c.IssuedVia, c.Note, c.Revoked, c.IsUsable,
                    }),
                });
            }

            case RemoteEnable:
            {
                var remote = HubRuntime.Remote;
                if (remote is null)
                    return ErrorResult("This hub build has no remote support.");

                var current = remote.Store.Settings;
                var bind = TryGetStringProp(args, "bindAddress") ?? current.BindAddress;
                if (!System.Net.IPAddress.TryParse(bind, out _))
                {
                    return ErrorResult(
                        $"'{bind}' is not a valid IP address to bind. Use 127.0.0.1 for a tunnelled setup, " +
                        "a specific interface address, or 0.0.0.0 for all interfaces.");
                }

                var port = TryGetIntProp(args, "port") ?? current.Port;
                if (port is <= 0 or > 65535)
                    return ErrorResult($"Port {port} is out of range.");

                try
                {
                    var message = await remote.EnableAsync(current with
                    {
                        BindAddress = bind,
                        Port = port,
                        AdvertisedEndpoint = TryGetStringProp(args, "advertisedEndpoint") ?? current.AdvertisedEndpoint,
                    }).ConfigureAwait(false);
                    return JsonResult(new
                    {
                        enabled = true,
                        listening = remote.IsListening,
                        boundEndpoint = remote.BoundEndpoint,
                        message,
                        // Say this plainly: opening the listener grants nothing on its own, and
                        // an operator who stops here will otherwise wonder why nothing connects.
                        next = "No client can attach until it holds a credential. Call hub_remote_issue, "
                            + "use the hub window, or run keincheck-enroll on the machine that will connect.",
                    });
                }
                catch (Exception ex)
                {
                    return ErrorResult($"Failed to enable remote access: {ex.Message}");
                }
            }

            case RemoteDisable:
            {
                var remote = HubRuntime.Remote;
                if (remote is null)
                    return ErrorResult("This hub build has no remote support.");

                await remote.DisableAsync().ConfigureAwait(false);
                return JsonResult(new { enabled = false, listening = remote.IsListening });
            }

            case SetReadOnly:
            {
                if (!TryGetClientId(args, out var id, out var err))
                    return ErrorResult(err);

                if (args is not { ValueKind: JsonValueKind.Object } o
                    || !o.TryGetProperty("readOnly", out var flag)
                    || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return ErrorResult("Missing required argument: 'readOnly' (true or false).");
                }

                var info = broker.ClientStatus(id);
                if (info is null)
                    return DownClientError(id, "is not known to the hub");

                // Deliberately a cast rather than a new IClientBroker member: keeping that
                // interface unchanged was an explicit goal of the remote work, and this is an
                // operator control rather than part of the broker contract the MCP proxy needs.
                if (broker is not PipeClientBroker concrete)
                    return ErrorResult("This hub's broker does not support toggling read-only.");

                var value = flag.ValueKind == JsonValueKind.True;
                concrete.SetReadOnly(id, value);

                return JsonResult(new
                {
                    clientId = id,
                    readOnly = value,
                    host = info.Host,
                    remembered = true,
                    effect = value
                        ? "Mutating tools are refused for this client."
                        : "Mutating tools are allowed. Remembered for this machine across "
                          + "reconnects and hub restarts; call again with readOnly=true to revoke.",
                });
            }

            case RemoteIssue:
            {
                var remote = HubRuntime.Remote;
                if (remote is null)
                    return ErrorResult("This hub build has no remote support.");

                var target = TryGetStringProp(args, "target");
                if (target is null)
                    return ErrorResult(
                        "Missing required argument: 'target' — the machine label this credential "
                        + "authenticates as (letters, digits, '.', '-' and '_').");

                var days = TryGetIntProp(args, "days");
                try
                {
                    var (bundle, record) = remote.Issue(
                        target,
                        days is > 0 ? TimeSpan.FromDays(days.Value) : null,
                        TryGetStringProp(args, "note"));

                    // Optional, because the credential has to reach another machine somehow and
                    // a file is usually the easiest way to carry it.
                    string? written = null;
                    if (TryGetStringProp(args, "outPath") is { } outPath)
                    {
                        try
                        {
                            var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
                            if (!string.IsNullOrEmpty(directory))
                                System.IO.Directory.CreateDirectory(directory);
                            File.WriteAllText(outPath, bundle);
                            written = Path.GetFullPath(outPath);
                        }
                        catch (Exception ex)
                        {
                            // The credential exists and is returned regardless; only the
                            // convenience copy failed, and losing it silently would be worse.
                            written = $"(could not write '{outPath}': {ex.Message})";
                        }
                    }

                    return JsonResult(new
                    {
                        bundle,
                        host = record.Host,
                        serial = record.Serial,
                        notAfter = record.NotAfter,
                        writtenTo = written,
                        listening = remote.IsListening,
                        next = remote.IsListening
                            ? $"Set KEINCHECK_REMOTE to the bundle on {record.Host}, or point "
                              + "KEINCHECK_REMOTE_FILE at a file holding it."
                            : "The listener is not running — call hub_remote_enable before the "
                              + "client tries to attach.",
                    });
                }
                catch (Exception ex)
                {
                    return ErrorResult($"Could not issue a credential for '{target}': {ex.Message}");
                }
            }

            case RemoteRevoke:
            {
                var remote = HubRuntime.Remote;
                if (remote is null)
                    return ErrorResult("This hub build has no remote support.");

                var serial = TryGetStringProp(args, "serial");
                if (serial is null)
                    return ErrorResult("Missing required argument: 'serial' (see hub_remote_status).");

                return remote.Revoke(serial)
                    ? JsonResult(new
                    {
                        revoked = serial,
                        // Deliberately not a lie: an already-established session keeps running.
                        // Revocation is checked on connect, so it lands on the next reconnect.
                        effect = "That credential is refused from its next connection attempt. "
                            + "An already-connected session continues until it drops.",
                    })
                    : ErrorResult($"'{serial}' is already revoked or not a credential this hub issued.");
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
- `hub_status` — the hub's own version, protocol version, active client, and connected client
  count (per-client builds appear as `clientVersion` in the client lists).
- `hub_set_readonly { clientId, readOnly }` — allow or forbid mutating tools for one client.
  Remote clients start read-only, so this is how you get permission to drive one.
- `hub_launch_client { clientId }` / `hub_restart_client { clientId }` — start / restart a
  known app by its recorded executable path. A restarted client keeps the **same id**.
- `hub_record_start { name? }` / `hub_record_stop` / `hub_record_status` — record the
  proxied UI tool calls you make.
- `hub_replay { stopOnError?, delayMs? }` — re-issue the recorded steps.
- `hub_export_test { format? }` — export the recording as `json` or a `csharp` xUnit skeleton.
- `hub_remote_status` / `hub_remote_enable` / `hub_remote_disable` / `hub_remote_issue` /
  `hub_remote_revoke` — attach apps running on **other machines**. See below.

## Apps on other machines

A hub normally only sees apps on its own machine. It can also broker apps running elsewhere —
a headless box, a device on the bench — and **you drive them with exactly the same tools**. A
remote client is just a client that has a host:

```
protoface#1                 <- local, on this machine
protoface@OP3R4T0RV2#1      <- remote, on the machine labelled OP3R4T0RV2
```

`hub_list_clients` shows `host`, `transport` (`pipe` or `tcp`), and `canLaunch` for each.

### Making an app remotely debuggable

The app must opt in at build time; the hub cannot reach into an app that did not. Tell the
user to do this in the app they want to reach — it is three steps and none of them is
something you can do for them from here:

1. **Add the `Keincheck.Remote` package** to that app. It is deliberately separate from
   `Keincheck.Client`: an app that does not reference it contains no networking code at all,
   so it cannot be reached remotely even by accident.

2. **Set the connector** where the app already calls `UseMcpClient`:

   ```csharp
   builder.UseMcpClient(o =>
   {
       o.AppId = "protoface";
       o.Connector = RemoteChannelConnector.FromEnvironment();   // KEINCHECK_REMOTE[_FILE]
   });
   ```

   `FromEnvironment()` returns null when no credential is configured, so the same build still
   attaches over the local pipe on a developer's machine.

3. **Give it a credential.** On the hub side: `hub_remote_enable`, then
   `hub_remote_issue { "target": "OP3R4T0RV2" }`. Hand the returned bundle to that machine as
   the `KEINCHECK_REMOTE` environment variable (or write it to a file and set
   `KEINCHECK_REMOTE_FILE`). The label you pass as `target` becomes the `@host` in the
   client's id, so pick the machine's real name.

If the app has no inbound route (behind NAT, roaming), forward a port instead of exposing one —
the client always dials, so from the hub's machine: `ssh -R 7423:127.0.0.1:7423 OP3R4T0RV2`.

### What is different about a remote client

- **Read-only by default.** Inspection works immediately; mutating tools (`click_at`,
  `type_text`, `set_property`, ...) are refused until read-only is lifted. To drive a remote
  app, call `hub_set_readonly { "clientId": "protoface@OP3R4T0RV2#1", "readOnly": false }`
  first. The decision is remembered for that machine across reconnects and hub restarts, so
  you only do it once per target — and `hub_list_clients` shows the current state.
- **Never auto-selected.** A local client can become active on its own; a remote one never
  does. Always `hub_select_client` explicitly.
- **Cannot be launched or restarted.** `canLaunch` is false and `hub_launch_client` /
  `hub_restart_client` will refuse — the process is on another machine. When a remote client
  drops, it reconnects on its own: use `hub_wait_for_client { "appId": "protoface@OP3R4T0RV2" }`
  rather than trying to restart it.
- **Disambiguate by host.** With a local *and* a remote instance of the same app connected,
  `{ "appId": "protoface" }` may match either. Use `protoface@OP3R4T0RV2` to be specific.

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
- **A client dropped?** Calls to it return a `client_unavailable` error naming the recovery
  tool. For a local client that is `hub_restart_client`, which keeps the **same id** so a
  recording still replays; if it was active and reconnects on its own (a rebuild loop) it
  re-becomes active with no re-select. For a **remote** client the error names
  `hub_wait_for_client` instead — the hub cannot restart a process on another machine, and
  the client dials back in by itself.
- **Blank screenshots?** A **locked workstation** renders nothing — screenshots come back
  blank. Unlock the session (or expect empty captures) before relying on vision.
- **Read-only clients** refuse mutating tools; `hub_client_status` shows the flag. Remote
  clients start read-only, so this is the normal state there rather than an unusual one.
- **Two clients with the same app id?** Check `host`. `protoface#1` and
  `protoface@OP3R4T0RV2#1` are different machines running the same app, and a bare
  `{ "appId": "protoface" }` filter may match either.
""";

    // ---- structured errors ------------------------------------------------

    /// <summary>
    /// The structured error returned when the AI calls a tool against a client that is
    /// down or unknown. It is a <see cref="CallToolResult"/> with <c>IsError</c> set and
    /// a machine-readable <c>structuredContent</c> object that names the recovery tool
    /// (<see cref="RestartClient"/>) so the model can self-heal.
    /// </summary>
    public static CallToolResult DownClientError(string clientId, string reason, ClientInfo? info = null)
    {
        // A remote client must NOT be told to restart itself: the hub cannot start a process
        // on another machine, so following that advice wastes a call and teaches the model a
        // recovery that never works. Wait for it to dial back in instead -- which is what
        // actually happens, since the client reconnects with backoff on its own.
        var isRemote = info?.IsRemote == true || info?.CanLaunch == false;

        string text;
        JsonObject recovery;
        if (isRemote)
        {
            var where = info?.Host is { Length: > 0 } host ? $" on {host}" : string.Empty;
            text =
                $"Client '{clientId}' {reason}. It runs{where}, so the hub cannot restart it. "
                + $"It reconnects on its own — call {WaitForClient} with "
                + $"{{ \"appId\": \"{clientId}\" }} to block until it is back, or "
                + $"{SelectClient} a different one ({ListClients} shows live clients).";
            recovery = new JsonObject
            {
                ["tool"] = WaitForClient,
                ["arguments"] = new JsonObject { ["appId"] = clientId, ["timeoutMs"] = 30000 },
            };
        }
        else
        {
            text =
                $"Client '{clientId}' {reason}. It cannot service tool calls right now. "
                + $"Call {RestartClient} with {{ \"clientId\": \"{clientId}\" }} to bring it "
                + $"back, or {SelectClient} a different one ({ListClients} shows live clients).";
            recovery = new JsonObject
            {
                ["tool"] = RestartClient,
                ["arguments"] = new JsonObject { ["clientId"] = clientId },
            };
        }

        var structured = new JsonObject
        {
            ["error"] = "client_unavailable",
            ["clientId"] = clientId,
            ["reason"] = reason,
            ["remote"] = isRemote,
            ["recovery"] = recovery,
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
        clientVersion = c.ClientVersion,
        readOnly = c.ReadOnly,
        toolCount = c.Tools.Count,
        executablePath = c.ExecutablePath,
        lastSeenUtc = c.LastSeenUtc,

        // Where this client actually is. Null host and "pipe" transport mean "on this
        // machine", so a purely local setup reads exactly as it did before. canLaunch is
        // surfaced so the model can see that hub_launch_client / hub_restart_client are not
        // available for a remote client BEFORE trying and getting an error.
        transport = c.Transport switch
        {
            ClientTransport.Pipe => "pipe",
            ClientTransport.Tcp => "tcp",
            ClientTransport.Relay => "relay",
            _ => "unknown",
        },
        host = c.Host,
        canLaunch = c.CanLaunch,
    };

    /// <summary>The set of currently-connected hub-ids, the live-membership source of truth for <c>connected</c>.</summary>
    private static IReadOnlySet<string> LiveIds(IClientBroker broker) =>
        broker.ListClients().Select(c => c.ClientId).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The hub's own informational version, reported by <c>hub_status</c>. Prefers the
    /// <see cref="AssemblyInformationalVersionAttribute"/> (e.g. "0.5.0"), strips any
    /// <c>+githash</c> build-metadata suffix for readability, and falls back to the plain
    /// assembly version. Best-effort: any failure yields null.
    /// </summary>
    private static readonly string? HubAssemblyVersion = ResolveHubAssemblyVersion();

    /// <summary>The hub's clean informational version (shared with the MCP server-version handshake).</summary>
    internal static string? ResolveHubAssemblyVersion()
    {
        try
        {
            var asm = typeof(HubMetaTools).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info))
                return asm.GetName().Version?.ToString();
            // Drop the SourceLink "+<commit>" build metadata so the reported version reads cleanly.
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        catch
        {
            return null;
        }
    }

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

    public static JsonElement SetReadOnlySchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"clientId":{"type":"string","description":"The hub-assigned client id (e.g. protoface@OP3R4T0RV2#1)."},"readOnly":{"type":"boolean","description":"true to refuse mutating tools, false to allow them."}},"required":["clientId","readOnly"]}""")
            .RootElement.Clone();

    public static JsonElement RemoteIssueSchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"target":{"type":"string","description":"The machine label this credential authenticates as. Becomes the '@host' in that client's id (e.g. protoface@OP3R4T0RV2). Letters, digits, '.', '-' and '_' only."},"days":{"type":"integer","description":"Validity in days; the hub clamps it to its own maximum (365)."},"note":{"type":"string","description":"Recorded against the credential in hub_remote_status."},"outPath":{"type":"string","description":"Also write the bundle to this file, which is usually the easiest way to carry it to the other machine."}},"required":["target"]}""")
            .RootElement.Clone();

    public static JsonElement RemoteRevokeSchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"serial":{"type":"string","description":"The credential serial from hub_remote_status."}},"required":["serial"]}""")
            .RootElement.Clone();

    public static JsonElement RemoteEnableSchema() =>
        JsonDocument.Parse(
            """{"type":"object","properties":{"bindAddress":{"type":"string","description":"IP address to bind. 127.0.0.1 (default) is reachable only through an SSH tunnel or another forwarder; a specific interface address or 0.0.0.0 exposes it on the network, where mutual TLS is the only barrier."},"port":{"type":"integer","description":"TCP port to listen on (default 7423)."},"advertisedEndpoint":{"type":"string","description":"host:port baked into newly-issued credentials so clients know where to dial."}}}""")
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
