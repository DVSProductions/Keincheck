# CI

Four workflows. Two make binaries, two check them.

| Workflow | Trigger | What it proves |
|---|---|---|
| [`ci.yml`](../.github/workflows/ci.yml) | push, PR | The solution builds and all ~360 unit tests pass |
| [`e2e.yml`](../.github/workflows/e2e.yml) | push, PR, dispatch | An **installed** hub really drives real apps |
| [`release.yml`](../.github/workflows/release.yml) | `v*.*.*` tag | Velopack release on GitHub |
| [`publish-nuget.yml`](../.github/workflows/publish-nuget.yml) | `v*.*.*` tag, dispatch | Library packages on NuGet.org |

Both build workflows run on `windows-latest`. That is not a preference: `Keincheck.Wpf` is
`net8.0-windows` with `UseWPF`, so `Keincheck.sln` cannot restore on Linux.

## What the E2E job actually does

Every other test in this repository runs in-process against stub brokers and in-memory
streams. None of them would notice if the hub binary failed to start, if
`keincheck-connect.exe` stopped shipping beside it, or if the packages stopped restoring.
The E2E job covers exactly that gap:

1. **Publishes** the hub (self-contained, win-x64, ReadyToRun) and the Native-AOT shim,
   using the same commands as `release.yml`, then copies the shim next to the hub.
2. **Packs** with `vpk` at version `99.0.0` and **installs** it with
   `Setup.exe --silent --installto`. The version is above every published release so
   Velopack's updater finds nothing newer — `HubOptions.AutoUpdate` is `true` and a real
   install genuinely does poll GitHub.
3. **Asserts the installed layout**: `current\Keincheck.Hub.exe`,
   `current\keincheck-connect.exe`, `Update.exe`. Nothing else checks that the shim is
   co-located, and the hub's "Set up in Claude" depends on it.
4. **Builds and runs** both demo apps and a throwaway consumer built from locally packed
   NuGet packages.
5. **Drives everything through `keincheck-connect.exe` over stdio** — the exact path Claude
   Code and Claude Desktop take.

The scenario, in order: handshake → `hub_status`/`hub_guide` with nothing attached →
`tools/list` is meta-only → the demo attaches → discovery → `tools/list` gains the client's
27 tools → a real button invoke changes a bound label → screenshots decode as real PNGs →
record, replay (and the counter moves *again*, so replay genuinely re-drove the UI), export
→ read-only refuses writes while still allowing reads → launch and restart keep the same
client id → the hub is unchanged and still serving.

Separate facts cover the named-pipe and loopback-HTTP transports, the WPF adapter, the
packaged consumer, remote access over loopback, and the shim starting a hub from cold.

## Running the E2E suite locally

**It drives a real hub and rewrites `%APPDATA%\Keincheck`.** Quit your own hub first — the
suite refuses to start if one is already running, because it would otherwise hijack it.

```powershell
$env:KEINCHECK_E2E            = "1"
$env:KEINCHECK_E2E_HUB_DIR    = "$env:LOCALAPPDATA\Keincheck.Hub\current"
$env:KEINCHECK_E2E_DEMO_EXE   = "samples\Keincheck.Demo\bin\Release\net10.0\Keincheck.Demo.exe"
$env:KEINCHECK_E2E_ARTIFACTS  = "$env:TEMP\kc-artifacts"

dotnet test tests\Keincheck.E2E
```

| Variable | Meaning |
|---|---|
| `KEINCHECK_E2E` | **The gate.** Nothing runs without `1`; every fact skips with a loud reason. |
| `KEINCHECK_E2E_HUB_DIR` | Directory holding **both** `Keincheck.Hub.exe` and `keincheck-connect.exe`. For a Velopack install that is `current`, never the install root — the exe in the root is the stub launcher, which re-execs and detaches. Omit it and the suite probes `Keincheck.Hub\bin\{Config}\net10.0`. |
| `KEINCHECK_E2E_DEMO_EXE` | The Avalonia demo to drive. |
| `KEINCHECK_E2E_WPF_DEMO_EXE` | Optional; enables the WPF adapter smoke. |
| `KEINCHECK_E2E_CONSUMER_EXE` | Optional; enables the packaged-consumer check. |
| `KEINCHECK_E2E_ARTIFACTS` | Where logs, screenshots and exports land. Defaults to a temp folder. |
| `KEINCHECK_E2E_REMOTE` | Opt-in. **Provisions a real certificate authority** into `%APPDATA%\Keincheck\remote` and installs the audit sink. |
| `KEINCHECK_E2E_SHIM_LAUNCH` | Opt-in. Requires **no hub running**; run it on its own. |

Why two guards (the env var *and* a `Category=E2E` trait `ci.yml` filters on): the failure
mode of an accidental run is destructive rather than noisy. The hub's pipe, mutex, port
3100 and `%APPDATA%\Keincheck` are fixed per-user names with no override — and `%APPDATA%`
in particular cannot be redirected, because `Environment.GetFolderPath` resolves it through
`SHGetKnownFolderPath`, which ignores the environment variable.

The suite cleans up after itself: `hub_set_readonly` is always restored to `false` (it
persists to `known-clients.json` and would otherwise cripple the app for good), and the
remote leg always revokes and disables.

## Reading a failure

The `e2e-artifacts` upload is the whole post-mortem:

| File | Use |
|---|---|
| `hub.log` | The hub's own stderr, plus a narrative of every scenario step and the shim's diagnostics |
| `demo.log`, `wpfdemo.log`, `consumer.log` | Each client's stderr, including its `[keincheck]` connect/reconnect trace |
| `screenshot_window.png`, `screenshot_marked.png` | **Look here first for anything UI-shaped.** A human can tell "black rectangle" from "no window" from "actual UI" in one glance |
| `exported-scenario.json`, `exported-test.cs` | What `hub_export_test` produced |
| `appdata/known-clients.json`, `appdata/*.jsonl` | The hub's persisted state and audit trail |
| `velopack-setup.log` | If the install itself went wrong |
| `*.trx` | Per-test results |

Key material is deliberately excluded: `*.pfx` and the remote `settings.json` hold private
keys and a PKCS#12 password.

**No retries, anywhere.** An E2E that is retried until green teaches nothing and hides
exactly the intermittent races it exists to catch. Instead every wait goes through
`hub_wait_for_client`, `wait_for`, `wait_for_idle`, or `PipeTransport.ConnectAsync`'s
backoff — there is not a single sleep in the suite.

## Known constraint: one job per machine

The hub is a per-user singleton by design. On GitHub-hosted runners each job gets its own
VM, so this costs nothing. On a self-hosted runner, two concurrent E2E jobs would fight
over the mutex, port 3100 and `%APPDATA%\Keincheck` — the `concurrency` group in `e2e.yml`
is there for that day.

## The Session 0 question

GitHub-hosted Windows runners execute jobs in a non-interactive session with a virtual
display and no `explorer.exe`. Avalonia windows are expected to create and render
(software fallback, no GPU), but the hub's `TrayIcon` calls `Shell_NotifyIcon`, which wants
a taskbar that is not there.

[`e2e-spike.yml`](../.github/workflows/e2e-spike.yml) exists to answer this and nothing
else: install, start, probe over the MCP pipe, and — the part that matters — check the hub
is **still alive afterwards**. `HubRuntime.Start` runs *before* Avalonia, so a hub can
answer a full MCP round-trip and still be a corpse a second later. Delete that workflow
once it has gone green once.

If it fails, in escalating cost: wrap `App.BuildTray` in a try/catch so a missing
notification area cannot kill the daemon; force `Win32RenderingMode.Software`; or move the
E2E to a self-hosted runner with a real logged-in session.
