# CI

Four workflows. Two make binaries, two check them.

| Workflow | Trigger | What it proves |
|---|---|---|
| [`ci.yml`](../.github/workflows/ci.yml) | main, PR, dispatch | The solution builds and the unit suite passes, on all three platforms |
| [`e2e.yml`](../.github/workflows/e2e.yml) | main, PR, dispatch | An **installed** hub really drives real apps |
| [`release.yml`](../.github/workflows/release.yml) | `v*.*.*` tag | Velopack releases (Windows, Linux, macOS) on GitHub |
| [`publish-nuget.yml`](../.github/workflows/publish-nuget.yml) | `v*.*.*` tag, dispatch | Library packages on NuGet.org |

`push` is scoped to `main` on the two test workflows. An unscoped `push:` also matches
pull-request branches *and* tag pushes, so every commit on an open PR ran twice and every
release tag re-ran a 15-minute job for a commit that had already been tested. Branch work is
covered by `pull_request`; use `workflow_dispatch` to run against a branch before opening one.

## Two solutions, one repo

`Keincheck.sln` cannot restore off Windows: `Keincheck.Wpf` is `net8.0-windows` with `UseWPF`,
and `samples/Keincheck.Wpf.Demo` with it. `Keincheck.CrossPlatform.slnf` is a solution filter
listing every *other* project, and it is what the Linux and macOS jobs build and test.

So `ci.yml` has two jobs:

| Job | Runner | Scope |
|---|---|---|
| `build-test` | `windows-latest` | `Keincheck.sln` — everything, including the WPF adapter |
| `build-test-unix` | `ubuntu-latest`, `macos-latest` | `Keincheck.CrossPlatform.slnf`, then a hub `dotnet publish` for that platform's RID |

The UI tests run on `Avalonia.Headless`, so the Unix jobs need no display server and no
`xvfb`. The extra publish step is there because `dotnet build` does not exercise the publish
path (RID-specific assets, the self-contained apphost, ReadyToRun cross-compilation), and a
release tag is the wrong place to find out that broke.

`e2e.yml` stays Windows-only: it installs a real Velopack `Setup.exe`.

**Adding a project?** Add it to `Keincheck.CrossPlatform.slnf` too, unless it is Windows-only.
A project missing from the filter is simply never built on Linux or macOS — silently.

## Cutting a release

Bump `<Version>` in `Directory.Build.props`, merge, then push a semver tag. **Tag a commit
that is already green** — the tag fires `release.yml` and `publish-nuget.yml` but *not* the
test workflows, by design (see above), so nothing re-checks the commit at release time.

```sh
git tag v0.11.0
git push origin v0.11.0
```

`release.yml` passes `-p:Version=` from the tag, so the tag is authoritative for the assembly
version — not just the installer's name. Without it the tray, `hub_status` and the update check
would report whatever `Directory.Build.props` happened to say.

`publish-nuget.yml` packs seven library packages and pushes them via NuGet Trusted Publishing
(GitHub OIDC — no stored API key). **NuGet versions are permanent**: a package can be unlisted
but never deleted, so a bad tag burns that version number.

To reproduce the Velopack packaging locally without burning a tag:

```sh
dotnet publish Keincheck.Hub/Keincheck.Hub.csproj -c Release -r win-x64 --self-contained true -o publish
dotnet publish Keincheck.Connect/Keincheck.Connect.csproj -c Release -r win-x64 -o publish-shim
cp publish-shim/keincheck-connect.exe publish/
vpk pack -u Keincheck.Hub -v 0.11.0 -p publish -e Keincheck.Hub.exe --packTitle "Keincheck Hub"
```

That stops short of `vpk upload`, which is what the workflow does with its own token. Copying
the shim into `publish/` is not optional — the hub's "Set up AI assistant" points the client at
the co-located `keincheck-connect.exe`, and `e2e.yml` asserts it is there.

### The three platform jobs

`release.yml` runs `windows` → `linux` → `macos` (arm64, then x64) → `notes`, **in sequence**.
They all upload into one GitHub release; Windows creates it and the rest pass `--merge`. Run in
parallel, two jobs race to create the same release and one loses with a 422.

Each platform gets its own Velopack **channel**, which is what keeps the update feeds apart —
a Linux install must never be offered a `win-x64` package. Windows keeps Velopack's default
channel (`win`); renaming it would orphan every existing install's update feed.

Two things about those jobs are load-bearing and non-obvious:

- **The macOS job must run on a macOS runner**, and not because `vpk` needs one. Apple Silicon
  refuses to execute an arm64 binary with *no* signature at all, and the ad-hoc signature that
  satisfies it is applied by the .NET SDK only when the publish itself runs on macOS. A
  cross-published `osx-arm64` build is an app that cannot start.
- **The Linux job installs `clang` and `zlib1g-dev`** before publishing the shim. Native AOT
  shells out to clang and links zlib; without them the publish fails with a link error that
  names neither package.

The macOS builds are ad-hoc signed but **not notarized** — that needs a paid Apple Developer
ID. They run; the first launch shows Gatekeeper's "cannot be opened…" dialog, which the user
clears once via System Settings → Privacy & Security → Open Anyway. If a Developer ID is ever
bought, `vpk pack` grows `--signAppIdentity` / `--notaryProfile` and the dialog goes away.

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
   co-located, and the hub's "Set up AI assistant" depends on it.
4. **Builds and runs** both demo apps and a throwaway consumer built from locally packed
   NuGet packages.
5. **Drives everything through `keincheck-connect.exe` over stdio** — the exact path an MCP
   client takes, whichever assistant the hub was set up for.

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

## The Session 0 question — settled

GitHub-hosted Windows runners execute jobs in a non-interactive session with a virtual
display and no `explorer.exe`. The open question was whether the hub — an Avalonia `WinExe`
whose `App` builds a `TrayIcon` via `Shell_NotifyIcon` — could run there at all. It matters
more than it looks: `Program.Main`'s `finally` tears down the broker and the MCP servers if
Avalonia throws, so a UI failure takes the pipe down with it.

**It works.** The E2E job runs green on `windows-latest`: the hub starts and stays up, a
client attaches over the named pipe, `screenshot_window` and `screenshot_marked` return real
PNGs, and the remote and cold-start legs pass. No tray workaround, no forced software
rendering, no self-hosted runner needed.

If that ever regresses, the fallbacks in escalating cost are: wrap `App.BuildTray` in a
try/catch so a missing notification area cannot kill the daemon; force
`Win32RenderingMode.Software` in `BuildAvaloniaApp`; or move the E2E to a self-hosted runner
with a logged-in session.

Note that the harness asserts the hub is alive **after** the whole scenario, not merely that
it answered once — `HubRuntime.Start` runs before Avalonia, so a dying hub can still serve a
full MCP round-trip on its way out.
