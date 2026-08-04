# Keincheck

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/DVSProductions/Keincheck/blob/main/LICENSE)
[![Release](https://img.shields.io/github/v/release/DVSProductions/Keincheck?include_prereleases&sort=semver)](https://github.com/DVSProductions/Keincheck/releases)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)
![Avalonia](https://img.shields.io/badge/Avalonia-12-7B3FE4)

**Let an AI see and drive any [Avalonia](https://avaloniaui.net) app.** Keincheck
exposes a [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server over
your running Avalonia 12 UI — list windows, walk the visual/logical tree, read and
write control properties, invoke controls through UI Automation, send synthetic input,
capture screenshots, and read binding errors.

**Supported UI frameworks:** Avalonia 12 today; WPF in progress. The introspection engine
is framework-free and reaches the UI through a single neutral seam (`IUiAdapter`), so new
toolkits plug in as adapter packages without touching the engine.

> ### ✨ New — drive apps on *other machines*
>
> Until now the hub could only see apps on its own box. **[`Keincheck.Remote`](#remote) lets it
> broker apps running anywhere**: inspect and drive an Avalonia app on a headless device, a
> test rig, or a laptop across the room — from the same AI session, through *exactly the same
> tools*. A remote client is just a client that happens to have a host:
>
> ```
> protoface#1                 ← local
> protoface@OP3R4T0RV2#1      ← the machine on the bench
> ```
>
> Mutually-authenticated TLS with a hub-owned certificate authority, read-only until you say
> otherwise, and **opt-in by package** — an app that does not reference `Keincheck.Remote`
> links no networking code at all. [Set it up →](#remote)

Two deployment models share one introspection engine:

| | **Broker** (recommended) | **Embedded** |
|---|---|---|
| Server | A standalone **Hub** daemon — one MCP server for many apps | One MCP server **inside** your app |
| Your app pulls in | an adapter pkg, e.g. `Keincheck.Avalonia` (named-pipe, **no ASP.NET**) | `Keincheck` (Kestrel in-process) |
| Transport to the AI | stdio shim → hub (auto-starts the hub) | loopback HTTP `http://127.0.0.1:3001` |
| Extras | launch/restart apps, multi-app routing, tray + audit + read-only toggle | none — zero infrastructure |
| Use when | you want a clean, reusable, multi-app setup | you want a single app wired in one line |

## Quick start — broker

1. **Install the Hub** from the [latest release](https://github.com/DVSProductions/Keincheck/releases)
   (a self-updating [Velopack](https://velopack.io) app).
2. **Add the client** to your Avalonia app and give it a stable id:
   ```csharp
   using Keincheck.Avalonia;   // Avalonia adapter package — supplies UseMcpClient

   AppBuilder.Configure<App>()
       .UsePlatformDetect()
       .UseMcpClient(o => o.AppId = "myapp");   // connects to the hub over a named pipe
   ```
3. **Point your MCP client at the stdio shim** — e.g. a project `.mcp.json`:
   ```json
   { "mcpServers": { "keincheck-hub": { "type": "stdio", "command": "keincheck-connect" } } }
   ```
   The shim ensures the hub is running and bridges stdio ↔ hub.
4. **Drive it:** `hub_list_clients` → `hub_select_client("myapp#1")` → then the per-app
   tools (`list_windows`, `screenshot_window`, `set_property`, …) operate on your app.

## Quick start — embedded

```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseMcpServer();          // MCP server on http://127.0.0.1:3001
```

Point any MCP-capable client at `http://127.0.0.1:3001`.

## Architecture (broker)

```
   AI client (Claude Code / Desktop)
        │ stdio (MCP)
   ┌────▼──────────────┐  ensures-up / launches
   │ keincheck-connect │  (stdio shim)
   └────┬──────────────┘
        │ MCP over a named pipe
   ┌────▼────────────────────────────────────────────┐
   │ Keincheck.Hub  (Velopack daemon, tray)          │
   │  • MCP server: meta-tools + proxy of active app │
   │  • named-pipe broker  • registry + launcher     │
   │  • audit log • per-app read-only toggle         │
   └────┬────────────────────────┬───────────────────┘
        │ named pipe             │ named pipe
   ┌────▼──────────┐        ┌────▼──────────┐
   │ Your app      │        │ Another app   │   apps embed an adapter pkg
   │ +UseMcpClient │        │ +UseMcpClient │   (e.g. Keincheck.Avalonia)
   └───────────────┘        └───────────────┘
```

Tools execute **inside each app** (where the UI toolkit lives, reached through that app's
`IUiAdapter`); the hub is a framework-agnostic multiplexer that advertises the active
client's tools and forwards calls over the pipe.

## Tools

**Per-app UI tools** (run in the app, proxied by the hub): `list_windows`,
`get_logical_tree`, `get_visual_tree`, `query_controls`, `get_properties`, `get_property`,
`get_data_context`, `get_text`, `get_binding_errors`, `hit_test`, `get_focused_element`,
`screenshot_window`, `screenshot_control`, `set_property`, `automation_action`, `set_focus`,
`wait_for`, `pointer` / `click_at`, `scroll_at`, `type_text`, `send_keys`.

**Hub meta-tools** — always present, whichever app is active. Start with `hub_guide`, which
returns the whole workflow as a document the model can read before touching anything.

| | |
|---|---|
| *Discover & select* | `hub_guide`, `hub_list_clients`, `hub_list_known_clients`, `hub_client_status`, `hub_select_client`, `hub_wait_for_client`, `hub_status` |
| *Lifecycle* | `hub_launch_client`, `hub_restart_client` |
| *Permissions* | `hub_set_readonly` — allow or refuse mutating tools per client |
| *Record & replay* | `hub_record_start`, `hub_record_stop`, `hub_record_status`, `hub_replay`, `hub_export_test` |
| *Static tooling* | `hub_list_client_tools`, `hub_call_tool` — discover and call a client's tools by name, for agents that don't support dynamic tool lists |
| *[Remote](#remote)* | `hub_remote_status`, `hub_remote_enable`, `hub_remote_disable`, `hub_remote_issue`, `hub_remote_revoke` |

Remote adds **no new tools for driving** — a remote app is addressed and driven exactly like a
local one. The `hub_remote_*` tools only administer the listener and its credentials.

**Addressing:** stable per-session handles (`ctl-1a`) plus a CSS-ish selector engine
(`Button[Name=Save]`, `#Save`, `.toolGroup`, `Button.primary`, `StackPanel > TextBox`).
The `.class` selector matches author style-class membership (Avalonia `Classes="…"`);
frameworks without style classes match nothing.

## Remote

By default the hub only sees apps on its own machine — the control pipe is a local,
current-user-only channel. **`Keincheck.Remote` lets a hub broker apps running elsewhere**:
inspect and drive an Avalonia app on another box from the AI session on yours, through the
same tools. A remote client is just a client that happens to have a host.

**It is a separate package on purpose.** An app that does not reference `Keincheck.Remote`
links no socket or TLS code at all, so remote debuggability can never be switched on by
accident or left behind as latent attack surface. And a hub only listens once an operator
explicitly enables it.

### Setting it up

**1. Turn on the listener** (hub tray ▸ *Remote access…*, or `hub_remote_enable`). The first
time, this generates the hub's own certificate authority.

**2. Issue a credential** for the machine that will connect. **The hub is the only thing that
issues them** — it owns the certificate authority — and there are three ways to ask, which
compose rather than compete.

**(a) Just ask for one.** From the AI, the hub window, or a shell:

```
hub_remote_issue { "target": "OP3R4T0RV2" }
```

```sh
Keincheck.Hub.exe --issue-credential --target OP3R4T0RV2 --out cred.txt
```

All three carry the same authorization — anything running as you — so none is privileged over
the others. The command works whether or not a hub is running: it asks the running hub when
there is one, and reads the store directly when there is not.

**(b) Let the build ask, when nothing is available.** Opt in and the build calls the installed
hub for you:

```xml
<PropertyGroup>
  <KeincheckRemoteEnroll>true</KeincheckRemoteEnroll>
  <KeincheckRemoteTarget>OP3R4T0RV2</KeincheckRemoteTarget>
</PropertyGroup>
```

It writes to `obj/` (never the source tree), embeds the result as the
`Keincheck.Remote.Credential` resource, reuses the existing one until it nears expiry, and
**warns rather than failing** when there is no hub — a build machine without one must not break.

**(c) Ask once yourself, then point the build at it.** The CI case: get a credential with (a),
store it as a secret, and hand the build the path.

```xml
<PropertyGroup>
  <KeincheckRemoteCredentialFile>$(CI_SECRET_PATH)</KeincheckRemoteCredentialFile>
</PropertyGroup>
```

`KEINCHECK_REMOTE_FILE` works too. **(c) always wins over (b)** — if you supplied a credential
the build will never quietly mint a different one — and a supplied path that does not exist is
a hard error rather than a silent fallback.

Never commit a credential. Build-issued ones default to 90 days, hand-issued to 365.

**3. Point the app at the hub.** Install `Keincheck.Remote` and set the connector — see
[`samples/Keincheck.Demo/Program.cs`](https://github.com/DVSProductions/Keincheck/blob/main/samples/Keincheck.Demo/Program.cs) for the real thing:

```csharp
builder.UseMcpClient(o =>
{
    o.AppId = "protoface";
    o.Log = msg => Console.Error.WriteLine($"[keincheck] {msg}");

    // Returns null when neither KEINCHECK_REMOTE_FILE nor KEINCHECK_REMOTE is set, so the
    // same build still uses the local pipe on a developer's desk.
    o.Connector = RemoteChannelConnector.FromEnvironment();
});
```

Set `KEINCHECK_REMOTE` (the bundle) or `KEINCHECK_REMOTE_FILE` (a path to it) on the target
machine. Setting `o.Log` is worth doing: without it a failed attach reports only to
`Debug.WriteLine`, which a Release build compiles out.

**4. Give the client a route to the hub.** Either bind the hub to a reachable address:

```
hub_remote_enable { "bindAddress": "192.168.1.50", "port": 7423 }
```

...which needs an inbound firewall rule on the hub machine:

```powershell
New-NetFirewallRule -DisplayName "Keincheck Hub" -Direction Inbound `
    -Protocol TCP -LocalPort 7423 -Action Allow    # run elevated
```

...or, if the target has no inbound route (behind NAT, roaming), forward a port instead and
skip the firewall entirely. The client always dials, so a reverse forward works:

```sh
ssh -R 7423:127.0.0.1:7423 OP3R4T0RV2      # from the hub machine
```

The client then appears as `protoface@OP3R4T0RV2#1` in `hub_list_clients` and is driven with
exactly the same tools as a local app. **No new AI-facing tools for driving** — only
`hub_remote_status` / `enable` / `disable` / `issue` / `revoke` to administer the listener, and
`hub_set_readonly` to permit mutating tools (remote clients start read-only).

### What protects it

- **Mutual TLS.** The hub refuses any client it did not issue a certificate to, and the client
  refuses any hub that does not hold the CA it enrolled against. The second half matters as
  much as the first: tool *results* carry screenshots and full UI trees.
- **Identity from the credential.** A client's host label is the common name of the
  certificate the hub validated — not self-reported, and not read off the socket (which is
  always loopback through a tunnel anyway).
- **Read-only by default.** Remote clients start read-only; `hub_set_readonly` (or the tray)
  permits mutating tools. The decision is remembered per machine, keyed on `AppId@Host`, so
  allowing the suit cannot quietly allow a copy of the same app on your desk.
- **Never auto-selected.** Tool calls go to whichever client is active, so a remote client is
  never made active automatically — that would let whatever attached first receive your calls.
- **Cannot be launched.** The hub refuses to launch or restart a remote client rather than
  risk starting a *local* copy while you believe you restarted the remote one.
- **Revocable.** A credential baked into a shipped build is extractable from that build.
  Every issued credential is listed and can be revoked; it stops working on the next connect.
- **Audited.** Attach, detach, auth failure, issuance and revocation are recorded, and once
  remote is enabled the trail is also written to `%APPDATA%\Keincheck\remote\audit\*.jsonl` —
  beside the certificate authority that issued the credentials it records.

Binding a non-loopback address is allowed — mutual TLS, not the network boundary, is what
protects the hub — but it is always an explicit choice, and you will need a firewall rule.

### When it does not connect

| Symptom | Cause |
|---|---|
| The app never appears in `hub_list_clients`, and says nothing | No credential. `RemoteChannelConnector.FromEnvironment()` returned null, so it silently used the local pipe instead. Set `KEINCHECK_REMOTE_FILE`, and set `o.Log` so the client can tell you. |
| *"the remote credential … expired"* | Re-issue with `hub_remote_issue`. The client stops rather than retrying, on purpose — a doomed reconnect loop would handshake every few seconds forever. |
| *"The hub refused the session (revoked)"* | That credential was revoked, or was issued by a different hub. Issue a fresh one. |
| *"No Keincheck hub reachable at …"* | Nothing is listening at that address. Check `hub_remote_status` for `boundEndpoint`, and remember `hub_remote_enable` only takes effect on a new port after the listener rebinds. |
| Connects, then drops every ~20s | The client is not sending heartbeats. If you wrote your own client rather than using `UseMcpClient`, the hub's watchdog will evict it. |
| `click_at` is *"refused"* | Working as intended — remote starts read-only. `hub_set_readonly { clientId, readOnly: false }`. |
| `hub_restart_client` fails on a remote client | Also intended. The hub cannot start a process on another machine; it refuses rather than risk starting a local copy. Use `hub_wait_for_client` — remote clients reconnect on their own. |

The hub's audit trail (`%APPDATA%\Keincheck\remote\audit\*.jsonl`, and the tray window) records
every attach, detach and authentication failure with its reason.

## Projects

| Project | TFM | Role |
|---|---|---|
| `Keincheck.Protocol` | net8.0 | Zero-dependency wire: named-pipe transport, chunked framing, message DTOs |
| `Keincheck.Core` | net8.0 | **Framework-free** introspection engine: registry, selectors, serializer, the 27 UI tools, and the neutral `IUiAdapter` / `IUiDispatcher` seam (no UI-toolkit reference) |
| `Keincheck.Avalonia` | net8.0 | Avalonia 12 adapter: `AvaloniaUiAdapter` + `AvaloniaUiDispatcher` behind the seam, plus the Avalonia `UseMcpClient` |
| `Keincheck.Wpf` | net8.0-windows | WPF adapter — **in progress** (scaffolded `WpfUiAdapter`, real `WpfUiDispatcher`, `UseKeincheckClient`) |
| `Keincheck.Client` | net8.0 | **Framework-free** broker client (`BrokerClientHost.Start`) — named-pipe, **no ASP.NET** |
| `Keincheck.Hub` | net10.0 | The broker daemon: pipe server, registry, launcher/restart, MCP proxy, tray (Velopack) |
| `Keincheck.Connect` | net8.0 | The stdio shim an MCP client spawns |
| `Keincheck.Remote` | net8.0 | **Opt-in** mutual-TLS transport for attaching apps on *other machines* — see [Remote](#remote) |
| `Keincheck` | net8.0 | Embedded all-in-one server (`UseMcpServer`) — Core + the Avalonia adapter |
| `samples/Keincheck.Demo` | net10.0 | Demo Avalonia app wired as a client |
| `tests/*` | net8.0 / net10.0 | xUnit + Avalonia.Headless |

The engine is **framework-free**: `Keincheck.Core` knows nothing about any UI toolkit and
talks to the live UI only through the neutral `IUiAdapter` / `IUiDispatcher` seam. A new
framework plugs in by implementing that seam in its own adapter package (as
`Keincheck.Avalonia` does for Avalonia and `Keincheck.Wpf` is doing for WPF) — no engine
changes required.

Libraries target **net8.0** for broad compatibility; the desktop/test apps target
**net10.0** with `<RollForward>Major</RollForward>`. Design notes live in [`docs/`](https://github.com/DVSProductions/Keincheck/tree/main/docs).

## Build & test

```sh
dotnet build Keincheck.sln
dotnet test  Keincheck.sln
```

Every push and pull request runs that build and the full unit suite
([`ci.yml`](https://github.com/DVSProductions/Keincheck/blob/main/.github/workflows/ci.yml)), plus an end-to-end job
([`e2e.yml`](https://github.com/DVSProductions/Keincheck/blob/main/.github/workflows/e2e.yml)) that installs the hub from a real Velopack
installer, launches the demo apps, and drives them through `keincheck-connect.exe` — the
same path Claude takes. See [`docs/ci.md`](https://github.com/DVSProductions/Keincheck/blob/main/docs/ci.md) for what it covers and how to run it
locally.

The E2E suite lives in `tests/Keincheck.E2E` and is **opt-in**: it drives a real hub and
rewrites `%APPDATA%\Keincheck`, so it skips unless `KEINCHECK_E2E=1`, and refuses to start
if a hub is already running rather than hijacking yours.

## Releasing

Pushing a semver tag triggers the [release workflow](https://github.com/DVSProductions/Keincheck/blob/main/.github/workflows/release.yml), which
publishes the Hub as a Velopack release on GitHub (installer + update + delta packages):

```sh
git tag v0.10.0
git push origin v0.10.0
```

Locally, the same flow is:

```sh
dotnet publish Keincheck.Hub/Keincheck.Hub.csproj -c Release -r win-x64 --self-contained true -o publish
vpk pack -u Keincheck.Hub -v 0.10.0 -p publish -e Keincheck.Hub.exe --packTitle "Keincheck Hub"
vpk upload github --repoUrl https://github.com/DVSProductions/Keincheck --publish --releaseName "Keincheck Hub 0.10.0" --tag v0.10.0 --token <gh-token>
```

## Security

Keincheck grants full programmatic control of an app's UI. It is designed for
**local, trusted** development and automation:

- **Broker:** the control pipe is **current-user only**; the hub's MCP endpoint is bound to
  **loopback only**. The hub shows an "AI is driving _X_" indicator and offers a per-app
  **read-only** toggle (mutating tools are refused) and an audit log of every call.
- **Embedded:** the listener is **loopback only** (never `0.0.0.0`), but there is **no auth
  token** — any local process can drive the app. Enable it only in development / trusted
  contexts, ideally behind a debug-only flag.
- **Remote:** off unless enabled, then mutually-authenticated TLS — see [Remote](#remote).
  Note the boundary this does *not* move: anything already running as your user can read the
  hub's CA from `%APPDATA%` and mint credentials, exactly as it can already drive every
  registered app through the control pipe. Every local issuance path (the MCP tool, the tray
  window, `Keincheck.Hub.exe --issue-credential`) sits at that same boundary and is treated
  identically. The line that *is* drawn is by transport: a **remote** client can never mint
  further credentials, so one leaked build certificate cannot become a self-renewing grant.

## License

[MIT](https://github.com/DVSProductions/Keincheck/blob/main/LICENSE) © 2026 Valentino Saitz
