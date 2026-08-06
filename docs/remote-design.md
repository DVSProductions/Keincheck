# Keincheck Remote — high-level design

> **Status: IMPLEMENTED.** This document is kept as the original design record. The shipped
> shape differs from it in five ways, each noted inline below and summarised here. See the
> [Remote section of the README](../README.md#remote) for how to actually use it.
>
> | Sketched here | What shipped | Why |
> |---|---|---|
> | Shared token, phase 1 with "zero new auth" | **mTLS with a hub-owned CA** | The hub issues client certs and clients pin its root, so neither side talks to anyone the CA did not vouch for. It also makes identity free — see below. |
> | Phase 1 (SSH tunnel) then phase 2 (TLS) | **One code path; TLS always** | With hub-issued certs the tunnelled and direct cases differ only in *where you point it*. There is no plaintext mode to ship by accident, and no throwaway MVP code. |
> | Host stamped from the accepted connection | **Host = CN of the validated client cert** | §3 preferred socket-stamping over self-report, correctly rejecting self-report — but over an SSH tunnel the peer address is *always* `127.0.0.1`, so the socket carries no information. The credential does. |
> | `RemoteClientBroker` + `CompositeClientBroker` | **One broker, a listener seam** | A single registry makes id uniqueness, `DefaultClientId`, `ListClients()` (which `HubUpdater` consults before restarting), event ordering and audit correct by construction. A composite would re-derive all of them, and `App`/`HubViewModel` take the concrete broker. |
> | Remote as a hub capability | **`Keincheck.Remote` is a separate package** | An app that does not reference it links no socket or TLS code at all, so remote debuggability cannot be enabled by accident. |
>
> Two premises in the original text are wrong and were not implemented as written:
> **§5** has the hub dialling out — the shipped design keeps *client-dials, hub-accepts* on
> every path, which `ssh -R` preserves. **§6/§10**'s framing of the link as one-directional is
> inaccurate: the hub *sends* `InvokeTool`, so a rogue client that authenticated could receive
> the operator's calls and answer them. That is why remote clients never auto-activate.
>
> Still descoped, as agreed: the rendezvous relay (§4B), UDP beacon discovery (§7), and the
> region/diff screenshot codec (§7) — though the network path did get Brotli frame compression.
>
> **Verified.** Unit and integration coverage runs on every push (`ci.yml`). An opt-in
> end-to-end suite (`e2e.yml`) installs the hub from a real Velopack installer and drives the
> remote leg through the shipped binary: enable, issue, attach a demo via the real
> `UseMcpClient` + `RemoteChannelConnector.FromEnvironment()` stack over a real TCP socket,
> prove the guards hold, then revoke and disable. It also packs `Keincheck.Remote` and restores
> it into a consumer project, asserting the MSBuild targets travelled inside the package.
>
> Manually, cross-machine against `MACHINENAME` over `ssh -R`: attach, drive, read-only refusal,
> revocation, three kill/reconnect cycles keeping the same id, a 28 KB base64 PNG screenshot,
> clicking a real control, and a non-loopback LAN bind.
>
> **Still not verified:** a remote client reached across a real LAN *through* a firewall — the
> bind and accept path is proven, but adding the inbound rule needs elevation, so every
> cross-machine run went through an SSH forward. Nothing has run on Linux or macOS, where
> OpenSSL replaces SChannel and the certificate storage flags this design depends on behave
> differently. The tray Remote panel is written but has never been opened.

## 1. The problem

Today the client ↔ hub link is a **local named pipe** — `PipeTransport` creates it with
`PipeOptions.CurrentUserOnly` in the per-machine pipe namespace. That is a deliberate, good
default: zero-config, fast, and only the current user on the box can reach it.

The consequence is that **the hub can only see apps running on its own machine.** The moment the
app you want to inspect lives somewhere else, Keincheck can't reach it.

This bites in practice. An app running headless on a second machine (`MACHINENAME`) cannot be
driven from the dev box at all. The workaround is an *interactive scheduled task* running a
UIAutomation clicker in that machine's console session — because the dev-box Keincheck tools
connect to a dev-box hub over a pipe that cannot cross machines. That works, but it is a
workaround for a capability Keincheck should own directly.

**Remote closes that gap: introspect and drive an Avalonia app on another machine, from the AI
session on the operator's box, through the same tools already in use.**

Motivating uses, concretely:
- Drive an app on a headless or embedded target from the dev box — feed it input, read its
  state, confirm a deploy landed — with no session-1 UIA hack.
- CI smoke test: after the pull-updater installs a build on the target, attach and assert the app
  came up (windows present, a control has the expected value) against the *live* remote build.
- Field debugging: attach to a deployed device over its own network and see what its UI is
  actually doing.

## 2. Goals & non-goals

**Goals**
- A hub can broker clients that live on **other machines**, over an authenticated, encrypted
  transport.
- **One surface.** A remote client shows up in the same `hub_list_clients` and is driven by the
  same tools. A remote client is just a client that happens to have a `Host`. No new AI-facing
  tools.
- **Match the target's reality.** A deployed target may roam, may have no inbound route, and may
  host its own network. So the design favours *client-dials-out*, tolerates reconnects, and can
  work "anywhere with internet" — the same reasoning that made the CI updater a pull, not a push.
- **Secure by construction.** Encrypted, mutually authenticated, **read-only by default** for
  remote, consent-gated for mutation, audited, and never exposed on a public network interface.
- **Additive.** The local pipe stays the zero-config default and the fast path. Remote is opt-in
  and changes nothing for existing local use.

**Non-goals**
- Not a general remote-desktop or screen-share.
- Not a way to drive apps that don't embed `Keincheck.Client` / `Keincheck.Avalonia`.
- Not a public multi-tenant service.
- Not a replacement for SSH — in fact the MVP *rides* SSH.

## 3. Why this is a small change

Two existing seams do most of the work:

**Transport is already stream-agnostic.** `PipeChannel` is documented as *"a message-oriented
duplex channel over an arbitrary byte `Stream`"* and frames messages with `FrameCodec`. It is
constructed as `new PipeChannel(Stream, ownsStream)`. Nothing in it is pipe-specific — the
pipe-ness lives only in `PipeTransport`'s connect/accept helpers. **A TLS `NetworkStream` is a
drop-in.** The message set (`Register`, `Heartbeat`, `ToolList`, `InvokeTool`, `ToolResult`,
`ClientDown`) and the `ProtocolVersion` handshake do not change at all.

→ New work: a `StreamTransport` sibling of `PipeTransport` with `Connect`/`Accept` over TCP/TLS.
  (Rename `PipeChannel` → `MessageChannel` eventually; it was never really about pipes.)

**The broker is already an interface.** `IClientBroker` (ListClients / ClientStatus /
DefaultClientId / Launch / Restart / WaitForClient / **InvokeOnClient** / connect+update+down
events) is consumed by `HubMcpServer`; `PipeClientBroker` is just one implementation. Add a
`RemoteClientBroker` that accepts network sessions and speaks the same protocol, and a
`CompositeClientBroker` that merges local-pipe and remote clients into a single `ListClients`.
**`HubMcpServer` needs no change** — it already codes against the interface, not the concrete type.

→ New work: `RemoteClientBroker` + `CompositeClientBroker`; `HubMcpServer` untouched.

**`ClientInfo` needs one concept added: location.** Two `myapp` clients (dev box + remote machine) must
be distinguishable. Add `Host` / `MachineId` and a `Transport` tag (`pipe` | `tcp` | `relay`); the
hub id becomes `AppId@Host#n`, so the list reads `myapp@MACHINENAME`. Either extend
`RegisterMessage` with host/machine identity, or have the broker stamp it from the connection it
accepted (preferred — the client can't spoof its own host).

## 4. Topologies

Support two, because they answer different situations.

**A. Direct dial-in** (same LAN, at the desk). The remote client opens a TLS socket to the hub's
network listener, registers, and is driven. Simplest; works whenever the client can reach the hub.

**B. Dial-out via rendezvous** (the field). The remote machine has no inbound route and roams. So both the
hub *and* the client make **outbound** TLS connections to a small, always-on **rendezvous** that
pairs them by `AppId` + token and forwards bytes. NAT- and AP-proof, "anywhere with internet" —
the exact shape as the updater's pull. Run the Keincheck session end-to-end *through* the relay so
the relay only ever sees ciphertext (it forwards frames; it does not terminate TLS).

## 5. Rollout — MVP rides SSH

**Phase 1 — transport zero: TCP over an SSH tunnel.** Before any new auth or network code: add the
`StreamTransport` (framed protocol over a TCP loopback socket), then bridge with SSH
port-forwarding. The remote machine is already SSH-key-reachable. `ssh -L 7000:127.0.0.1:7000` maps
dev-box-localhost → remote-localhost; the hub dials the local end, the remote machine client speaks the same
framed protocol on its loopback end. **Zero new auth** (SSH keys are already the trust root),
already encrypted, reachable only by someone holding the key. Ships this week, proves the
stream-agnostic transport end to end, and remains a valid fallback forever. Remote clients default
to **read-only**; mutation is an explicit opt-in.

**Phase 2 — native remote.** TLS (`SslStream`) + token auth (reuse the updater's token pattern) so
you don't need an SSH session open on the same LAN. Reconnect-with-backoff on the client (the remote machine's
wifi flaps — the pattern already exists in `PipeTransport.ConnectAsync`). `Host`/`MachineId` in
`ClientInfo`. UDP beacon discovery on-LAN (the companion link already beacons on `:8766`) so the hub
auto-finds remote clients with no pinned address.

**Phase 3 — anywhere.** The rendezvous relay (dial-out both sides), a consent + visibility surface
(on the client UI: "a remote is attached", audit line on every mutating call — `HubAuditLog`
already exists), a wearer/paw kill-switch, and a network-tuned screenshot codec (see §7).

## 6. Security model

- **Encryption.** TLS on the direct path; end-to-end Keincheck-over-TLS through the relay so it
  never sees plaintext.
- **Auth.** A per-hub shared token (client presents it at/after `Register`; unauthenticated
  sessions are dropped), or mTLS with a small CA if we ever have many targets. Token first — it
  matches infra we already run.
- **Never on the AP.** The remote listener / relay endpoint binds loopback (SSH model) or an
  explicit private interface — **never** a public network interface. This is the same
  discipline the companion server and the loopback-only llama sidecar already follow.
- **Read-only by default for remote.** `ClientInfo.ReadOnly` already exists and the broker already
  refuses mutating tools for read-only clients. Remote clients start `ReadOnly = true`; "look at
  the remote machine" is always safe, and "drive the remote machine" is a deliberate per-session escalation.
- **Consent & visibility.** The wearer must be able to see that someone is driving them — an
  on-screen indicator and an audit line when a remote attaches and when a mutating call runs.
- **Kill switch.** A client-side disconnect the wearer (or a paw button) can trigger.

## 7. Identity, discovery, robustness

- **Identity.** `AppId@Host#n`; `ListClients` shows `myapp@MACHINENAME` vs `myapp@OTHERMACHINE`.
  Host is stamped by the broker from the accepted connection, not self-reported.
- **Discovery.** On-LAN: a UDP beacon (as the companion link already does) so the hub finds remote
  clients without a pinned address. In the field: the rendezvous is the directory (register by
  `AppId` + token).
- **Reconnect.** Heartbeats already exist. Add backoff-reconnect + session resume (re-`Register`,
  re-send `ToolList`) on the client, and treat a transport drop as `ClientDown` → later
  `ClientConnected` rather than a hard failure. A roaming client should reappear on its own.
- **Screenshots.** Full BGRA frames are big; the local pipe can stay raw, but the network path
  needs chunking + compression, and ideally region/diff frames. Frame-diffing is the standard
  answer here — worth doing rather than shipping whole frames.

## 8. Test record/replay implications

Recorded Keincheck tests target `AutomationId`s, so they are machine-agnostic. Remote makes two new
things possible: **record a real interaction on the field device and replay it in CI**, and **run an
existing recorded test against a live remote build** as a post-deploy smoke check. That folds
directly into the CI pull-updater: push → build → the device self-updates → hub attaches remotely →
replay a smoke test → assert the UI came up.

## 9. Open questions

- One hub brokering many remotes (the composite-broker shape), or a hub per target? Composite reads
  cleaner and keeps one AI surface.
- Shared token vs mTLS. Token is simpler and matches existing infra; mTLS scales better to many
  targets.
- Where does the rendezvous live — on the always-on GitLab box (already trusted, already the pull
  origin), or a tiny dedicated service?
- Screenshot bandwidth policy: full-frame vs region vs diff, and who decides.
- Does `Keincheck.Connect` (the current connect shim) become the place the remote dial-out /
  tunnel-setup logic lives, or is that a new small tool?

## 10. One-paragraph summary

The transport is already a framed protocol over an arbitrary `Stream`, and the broker is already an
interface. So "remote" is: a TLS/TCP transport beside the pipe, a `RemoteClientBroker` +
`CompositeClientBroker` beside `PipeClientBroker`, a `Host` field on `ClientInfo`, and — for the
roaming-client case — a thin dial-out rendezvous. Ship it in three steps: SSH-tunnel MVP (this week,
zero new trust), native TLS + token (no SSH session needed), then the rendezvous ("anywhere with
internet"). Read-only by default, mutation opt-in, never on the AP, audited. The AI-facing tool
surface does not change at all — a remote app is just a client with an address.
