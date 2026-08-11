# Keincheck browser demo

A browser-hosted Avalonia app that attaches to the hub over the WebSocket transport. It exists
to prove the one thing unit tests and `curl` cannot: that a **real browser** attaches, and that
an AI can then drive what it is showing.

It is deliberately **not** in `Keincheck.sln` — the browser target framework needs the
`wasm-tools` workload, which is not part of a default SDK install, so including it would break
`dotnet build` for anyone who has not installed it, including CI.

## Running it

```sh
dotnet workload install wasm-tools          # once; needs elevation on Windows
dotnet run --project samples/Keincheck.Browser.Demo
```

Then open <http://localhost:5000>. The port matters: the build declares
`KeincheckWebSocketOrigin=http://localhost:5000`, the hub allowlists that exact origin, and a
page served from anywhere else is refused with `403`.

The page says whether it attached. If it reports *not enrolled*, the build had no hub to enroll
against — check that the hub is installed and see the build warning.

## What it exercises

| Control | Tool it is there for |
|---|---|
| `CounterText`, `IncrementButton`, `ResetButton` | `get_text`, `click_at`, `automation_action` |
| `NameBox`, `EchoText` | `type_text`, `set_focus` |
| `EnabledCheck`, `ModeSwitch` | `set_property` on a bool |
| `LevelSlider`, `LevelText` | `set_property` on a double |
| `ItemList` and its items | `query_controls`, selection |

Every control has an `x:Name`, so selectors address it directly.

## How it differs from the desktop sample

Two lines, and no more:

```csharp
o.Connector = WebSocketChannelConnector.FromCredential();
```

plus applying `UseMcpClient` only when that returns non-null — because `BrokerClient` falls back
to the named-pipe connector when no connector is set, and a browser has no named pipes, so an
unenrolled build would throw rather than degrade.

It does **not** reference `Keincheck.Remote`. A browser has no sockets and no
`X509Certificate2`, so mutual TLS cannot work there; referencing it would only add code that
throws.
