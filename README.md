# Keincheck

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
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

**Hub meta-tools:** `hub_list_clients`, `hub_list_known_clients`, `hub_launch_client`,
`hub_restart_client`, `hub_select_client`, `hub_client_status`.

**Addressing:** stable per-session handles (`ctl-1a`) plus a CSS-ish selector engine
(`Button[Name=Save]`, `#Save`, `.toolGroup`, `Button.primary`, `StackPanel > TextBox`).
The `.class` selector matches author style-class membership (Avalonia `Classes="…"`);
frameworks without style classes match nothing.

## Projects

| Project | TFM | Role |
|---|---|---|
| `Keincheck.Protocol` | net8.0 | Zero-dependency wire: named-pipe transport, chunked framing, message DTOs |
| `Keincheck.Core` | net8.0 | **Framework-free** introspection engine: registry, selectors, serializer, the 22 tools, and the neutral `IUiAdapter` / `IUiDispatcher` seam (no UI-toolkit reference) |
| `Keincheck.Avalonia` | net8.0 | Avalonia 12 adapter: `AvaloniaUiAdapter` + `AvaloniaUiDispatcher` behind the seam, plus the Avalonia `UseMcpClient` |
| `Keincheck.Wpf` | net8.0-windows | WPF adapter — **in progress** (scaffolded `WpfUiAdapter`, real `WpfUiDispatcher`, `UseKeincheckClient`) |
| `Keincheck.Client` | net8.0 | **Framework-free** broker client (`BrokerClientHost.Start`) — named-pipe, **no ASP.NET** |
| `Keincheck.Hub` | net10.0 | The broker daemon: pipe server, registry, launcher/restart, MCP proxy, tray (Velopack) |
| `Keincheck.Connect` | net8.0 | The stdio shim an MCP client spawns |
| `Keincheck` | net8.0 | Embedded all-in-one server (`UseMcpServer`) — Core + the Avalonia adapter |
| `samples/Keincheck.Demo` | net10.0 | Demo Avalonia app wired as a client |
| `tests/*` | net8.0 / net10.0 | xUnit + Avalonia.Headless |

The engine is **framework-free**: `Keincheck.Core` knows nothing about any UI toolkit and
talks to the live UI only through the neutral `IUiAdapter` / `IUiDispatcher` seam. A new
framework plugs in by implementing that seam in its own adapter package (as
`Keincheck.Avalonia` does for Avalonia and `Keincheck.Wpf` is doing for WPF) — no engine
changes required.

Libraries target **net8.0** for broad compatibility; the desktop/test apps target
**net10.0** with `<RollForward>Major</RollForward>`. Design notes live in [`docs/`](docs/).

## Build & test

```sh
dotnet build Keincheck.sln
dotnet test  Keincheck.sln
```

## Releasing

Pushing a semver tag triggers the [release workflow](.github/workflows/release.yml), which
publishes the Hub as a Velopack release on GitHub (installer + update + delta packages):

```sh
git tag v0.2.0
git push origin v0.2.0
```

Locally, the same flow is:

```sh
dotnet publish Keincheck.Hub/Keincheck.Hub.csproj -c Release -r win-x64 --self-contained true -o publish
vpk pack -u Keincheck.Hub -v 0.2.0 -p publish -e Keincheck.Hub.exe --packTitle "Keincheck Hub"
vpk upload github --repoUrl https://github.com/DVSProductions/Keincheck --publish --releaseName "Keincheck Hub 0.2.0" --tag v0.2.0 --token <gh-token>
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

## License

[MIT](LICENSE) © 2026 Valentino Saitz
