# Keincheck v2 — Stage 2 (the BROKER) Contracts

This is the **ground truth** for the Phase-B Hub agents and the Client/Connect
finishers. Everything below is copied from the **built, green** solution (8 projects:
6 product + 2 test; `dotnet build` clean, `dotnet test` 94 pass / 1 skip). The three
uncertain MCP-SDK mechanics are **proven by running spikes**, not just compiled
(`tests/Keincheck.Tests/Stage2SpikeTests.cs`, 2/2 pass). Code against these exact
symbols; if you need a new member, ask the Foundation agent.

Stage-1 substrate is unchanged — see `CONTRACTS_V2.md` for `FrameCodec`,
`MessageEnvelope`/DTOs, `IUiAdapter`, `ControlRegistry`, the 22 `[McpServerTool]`s.

> **Root path note:** the solution lives at
> `M:\OneDrive\Programmieren\C#\Keincheck\Keincheck.sln`. (The task brief's
> `undefined\…` placeholders resolve to this directory.)

---

## 0. Solution layout (3 NEW projects, added to the .sln)

| Assembly                | TFM     | OutputType | References (new)                                                                 |
|-------------------------|---------|------------|---------------------------------------------------------------------------------|
| **Keincheck.Client**  | net8.0  | library    | → Core, → Protocol, `Microsoft.Extensions.DependencyInjection` 8.0.1. **No ASP.NET.** |
| **Keincheck.Hub**     | net10.0 | WinExe     | → Protocol, `ModelContextProtocol.AspNetCore` 1.4.0, Avalonia.Desktop/Themes.Fluent 12.0.4, Velopack 0.0.1298, `FrameworkReference Microsoft.AspNetCore.App`, `<RollForward>Major</RollForward>`. **Does NOT reference Core/Avalonia-introspection.** |
| **Keincheck.Connect** | net8.0  | Exe (`keincheck-connect`) | → Protocol only. No MCP SDK ref (pure byte-pump). |

SDK facts (ModelContextProtocol **1.4.0**, verified by reflection):
- `IMcpServerBuilder` is in namespace **`Microsoft.Extensions.DependencyInjection`**.
- `McpClient` / `StreamClientTransport` are in **`ModelContextProtocol.Client`** /
  **`ModelContextProtocol.Protocol`** (there is **no** `McpClientFactory` — use
  `McpClient.CreateAsync`).
- `McpServerTool`, `RequestContext<T>`, `McpServer`, handlers live in
  **`ModelContextProtocol.Server`**; `Tool`/`CallToolResult`/`ContentBlock` in
  **`ModelContextProtocol.Protocol`**.

---

## 1. Shared pipe transport (in **Protocol**, BCL-only, zero new deps)

### Well-known names — `Keincheck.Protocol.PipeNames`

```csharp
public static class PipeNames
{
    public static string UserScope           { get; } // sanitized Environment.UserName
    public static string ControlPipe         { get; } // "Keincheck.{user}"  — hub control pipe
    public static string SingleInstanceMutex { get; } // "Global\\Keincheck.Hub.{user}"
    public static string McpSessionPipe(string token); // "Keincheck.mcp.{user}.{token}"
}
```

Pass the **bare** name to `NamedPipe*Stream` — the BCL prepends `\\.\pipe\`.

### `Keincheck.Protocol.PipeChannel` — message-oriented duplex over a Stream

```csharp
public sealed class PipeChannel : IAsyncDisposable, IDisposable
{
    public PipeChannel(Stream stream, bool ownsStream = true);
    public Stream Stream { get; }
    public bool   IsDisposed { get; }

    public Task SendAsync(MessageEnvelope envelope, CancellationToken ct = default);
    public Task SendAsync<T>(MessageKind kind, T message, string? correlationId = null, CancellationToken ct = default);
    public Task<MessageEnvelope?> ReceiveAsync(CancellationToken ct = default); // null on clean EOF between frames
}
```

Internally: `JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options)` →
`FrameCodec.WriteAsync`; reads do the reverse. Writes are serialized by a semaphore so
concurrent senders never interleave chunks.

### `Keincheck.Protocol.PipeTransport` — connect/accept helpers

```csharp
public static class PipeTransport
{
    public const int DefaultMaxServerInstances = 32;

    public static NamedPipeServerStream CreateServerStream(string? pipeName = null,
        int maxServerInstances = DefaultMaxServerInstances);            // Byte mode, Async|CurrentUserOnly
    public static Task<PipeChannel> AcceptAsync(NamedPipeServerStream server, CancellationToken ct = default);
    public static Task<PipeChannel> ConnectAsync(string? pipeName = null,  // retry/backoff to deadline
        TimeSpan? timeout = null, CancellationToken ct = default);        // throws TimeoutException if hub never came up
    public static bool IsWindows { get; }
}
```

**Security:** both server and client use `PipeOptions.CurrentUserOnly` → the pipe is
ACL-restricted to the current user with **no extra package** (no
`System.IO.Pipes.AccessControl` needed). Pipe is local namespace + per-user-named.
On non-Windows the BCL maps to a UDS under temp (best-effort; Windows is the live target).

---

## 2. MECHANIC #1 — Client builds Core tools, extracts schema, invokes (PROVEN)

**Exact 1.4.0 API used (verified):**
- `McpServerTool.Create(MethodInfo method, object target, McpServerToolCreateOptions options)`
  — `target` is `null!` for the **static** `[McpServerTool]` methods. DI params come
  from `options.Services` (`IServiceProvider`); the rest bind from JSON args at invoke.
- Read schema from `tool.ProtocolTool` (`ModelContextProtocol.Protocol.Tool`):
  `.Name` (string), `.Description` (string?), `.InputSchema` (**`JsonElement`**, always
  an object schema — empty params ⇒ `{"type":"object","properties":{}}`).
- Invoke: `ValueTask<CallToolResult> tool.InvokeAsync(RequestContext<CallToolRequestParams>, CancellationToken)`.
  `CallToolResult.Content` is `IList<ContentBlock>`; `.IsError` is `bool?`.
- Build the request context **out-of-band** (no live MCP session):
  `new RequestContext<CallToolRequestParams>(server, jsonRpcRequest, parameters) { Services = provider }`.
  The `Services`/`Server` setters are on the base `MessageContext`. A **parked**
  `McpServer` (created over an in-memory `System.IO.Pipelines.Pipe`, never run)
  satisfies the context shape; the SDK resolves DI from `options.Services`, captured at
  tool creation — **so a live connection is NOT required to invoke.**

### Public surface — `Keincheck.Client`

```csharp
public sealed class ClientToolHost : IDisposable
{
    public static ClientToolHost Build(Avalonia.Application app,
        Keincheck.Core.McpServerOptions options, params Assembly[] additionalToolAssemblies);
    public IReadOnlyDictionary<string, McpServerTool> Tools { get; }
    public IReadOnlyList<ToolDescriptor> Describe();                    // -> ToolListMessage.Tools
    public Task<CallToolResult> InvokeAsync(string toolName, JsonElement? argumentsJson, CancellationToken ct = default);
}
```

`Build` registers the **same DI singletons `McpHost` does** — `Application`,
`McpServerOptions`, `ControlRegistry`, `PropertyValueSerializer`, optional
`BindingErrorSink`, and `IUiAdapter` (`AvaloniaUiAdapter`) — then materializes every
`[McpServerTool]` in `typeof(InspectionTools).Assembly` (+ extras).

`Describe()` maps each tool to a `Protocol.ToolDescriptor { Name, Description,
InputSchema = ProtocolTool.InputSchema.Clone() }`.

### `McpClientOptions` (public)

```csharp
public sealed class McpClientOptions
{
    public string? AppId;            // default: entry assembly name
    public string? DisplayName;      // default: AppId
    public bool    ReadOnly;         // refuse mutating tools locally
    public string? PipeName;         // default PipeNames.ControlPipe
    public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public bool    AutoReconnect = true;
    public Keincheck.Core.McpServerOptions? CoreOptions;
}
```

### App entry point + run loop

```csharp
namespace Keincheck.Client;
public static class AppBuilderClientExtensions {
    public static AppBuilder UseMcpClient(this AppBuilder builder, Action<McpClientOptions>? configure = null);
}
public sealed class BrokerClient : IAsyncDisposable {
    public static BrokerClient Start(Avalonia.Application app, McpClientOptions options);
    public string ClientId { get; }
}
```

`BrokerClient` already: connects (retry/backoff) → `Register` → `ToolList` →
loop{`InvokeTool`→run Core tool→`ToolResult`} + `Heartbeat` pump + auto-reconnect +
graceful `ClientDown`. Tool results are sent as
`ToolResultMessage.Content = JsonSerializer.SerializeToElement(callResult.Content, ProtocolJson.Options)`
(a JSON **array** of MCP content blocks). **Phase-B for Client:** derive read-only-ness
from `tool.ProtocolTool.Annotations` instead of the name heuristic in
`BrokerClient.IsReadOnlyTool`.

---

## 3. MECHANIC #2 — Hub dynamic tools (list/call handlers + list_changed) (PROVEN)

**Exact 1.4.0 API used (verified):**
- `IMcpServerBuilder` extension methods (namespace `Microsoft.Extensions.DependencyInjection`):
  `WithListToolsHandler(McpRequestHandler<ListToolsRequestParams,ListToolsResult>)`,
  `WithCallToolHandler(McpRequestHandler<CallToolRequestParams,CallToolResult>)`.
- The handler delegate is
  `delegate ValueTask<TResult> McpRequestHandler<TParams,TResult>(RequestContext<TParams> request, CancellationToken ct)`
  (namespace `ModelContextProtocol.Server`).
- Advertise mutable list: `serverOptions.Capabilities.Tools.ListChanged = true`
  (`ServerCapabilities.Tools` is `ToolsCapability { bool? ListChanged }`).
- Emit list_changed: `await server.SendNotificationAsync(
  NotificationMethods.ToolListChangedNotification, CancellationToken.None)` where the
  `McpServer` is obtained from `request.Server` inside the list handler (capture each
  session's server). Constant value = `"notifications/tools/list_changed"`.
- A tool entry is a `Protocol.Tool { Name, Description, InputSchema (JsonElement) }`.
  `ListToolsResult.Tools` is `IList<Tool>`. `CallToolRequestParams.Arguments` is
  `IDictionary<string, JsonElement>`; `CallToolResult.Content` is `IList<ContentBlock>`,
  `TextContentBlock { string Text }`, `ImageContentBlock.FromBytes(ReadOnlyMemory<byte>, string mimeType)`.

### Public surface — `Keincheck.Hub`

```csharp
public sealed class HubMcpServer : IAsyncDisposable
{
    public static HubMcpServer Start(IClientBroker broker, HubOptions options); // starts loopback HTTP MCP
    public IMcpServerBuilder ConfigureMcp(IMcpServerBuilder mcp);               // wires the SAME dynamic handlers (reused by the pipe host)
    public Task NotifyToolListChangedAsync();                                   // broadcast to all live MCP sessions
}

public sealed class HubOptions
{
    public int     HttpPort = 3100;        // loopback only
    public bool    ServeMcpOverPipe = true;
    public string? PipeName;               // default PipeNames.ControlPipe
    public string  ServerName = "Keincheck.Hub";
    public string  ServerVersion = "0.1.0";
    public TimeSpan InvokeTimeout = TimeSpan.FromSeconds(60);
    public bool    QualifyToolNames;       // false => active client's names passed through verbatim
}
```

The dynamic catalog = 3 meta-tools (`hub_list_clients`, `hub_list_known_clients`,
`hub_select_client`) **plus** the **active** client's `ToolDescriptor`s re-advertised
**verbatim** (schemas as the client reported — the hub never sees Core/Avalonia).
`tools/call` dispatches: meta-tools handled in-hub; everything else →
`broker.InvokeOnClientAsync(activeId, toolName, args)` and the
`ToolResultMessage.Content` (array of blocks) is deserialized back into
`IList<ContentBlock>`. A change to the active client raises list_changed.

---

## 4. MECHANIC #3 — Connect stdio bridge over the pipe stream (PROVEN, preferred path)

**The strongly-preferred path works:** the SDK can run its **server** over an arbitrary
`Stream` via `new StreamServerTransport(input, output, serverName, loggerFactory)` +
`McpServer.Create(transport, serverOptions, loggerFactory, serviceProvider)` +
`server.RunAsync(ct)`. So the hub serves MCP over a **named pipe**, and `Connect` is a
**pure byte-pump** (no MCP parsing, no MCP SDK reference).

Test client side (also SDK): `new StreamClientTransport(serverInput, serverOutput, loggerFactory)`
+ `await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, ct)`
→ `initialize` + `ListToolsAsync()` + `CallToolAsync(name, args, ct)` all flow end to end.

### Hub side — `Keincheck.Hub.HubPipeMcpListener`

```csharp
public sealed class HubPipeMcpListener : IAsyncDisposable
{
    public HubPipeMcpListener(HubMcpServer hub, HubOptions options);
    public void Start(string? pipeName = null);                          // accept loop on the pipe
    public Task RunMcpOverStreamAsync(Stream input, Stream output, CancellationToken ct); // one session
}
```

Each accepted pipe connection gets its own `StreamServerTransport` + `McpServer` wired
with `hub.ConfigureMcp(...)` (the same handlers as HTTP). `RunMcpOverStreamAsync` is
public so it can be driven over an in-memory `Pipe` pair in tests.

### Shim — `Keincheck.Connect` (exe `keincheck-connect`)

Args: `--pipe <name>` (default `PipeNames.McpSessionPipe("default")`),
`--hub-exe <path>`. Flow: `EnsureHubRunningAsync` (single-instance mutex
`PipeNames.SingleInstanceMutex` via `Mutex.TryOpenExisting`; launches the hub exe if
absent — co-located `Keincheck.Hub.exe` by default) → `PipeTransport.ConnectAsync`
→ byte-pump `stdin→pipe` / `pipe→stdout`. **All logs to stderr; stdout is MCP-only.**

> **Phase-B alignment:** the spike's `HubPipeMcpListener` accepts on a single pipe name.
> The production hub should accept on `PipeNames.ControlPipe`, dedicate the **control**
> pipe to the wire-Protocol clients (Register/ToolList/Invoke), and serve **MCP** on a
> separate name (e.g. `McpSessionPipe(token)`) handed to each `Connect`. The two stream
> hosts share `HubMcpServer.ConfigureMcp` verbatim. Fallback (stdio↔loopback-HTTP relay)
> is NOT needed — the stream path is proven.

---

## 5. Hub-internal contract (shared by the two Hub agents)

```csharp
namespace Keincheck.Hub;

public interface IClientBroker
{
    IReadOnlyList<ClientInfo> ListClients();        // live pipe sessions
    IReadOnlyList<ClientInfo> ListKnownClients();   // live + previously-seen/persisted
    ClientInfo? ClientStatus(string clientId);
    string?     ActiveClientId { get; set; }        // set => raises ClientUpdated => list_changed

    Task<int> LaunchClientAsync(string clientId, CancellationToken ct = default);
    Task<int> RestartClientAsync(string clientId, CancellationToken ct = default);
    Task<ToolResultMessage> InvokeOnClientAsync(string clientId, string toolName,
        System.Text.Json.JsonElement? argumentsJson, CancellationToken ct = default);

    event EventHandler<ClientInfo>? ClientConnected;
    event EventHandler<ClientInfo>? ClientUpdated;  // metadata/catalog change
    event EventHandler<ClientInfo>? ClientDown;
}

public sealed record ClientInfo
{
    public required string ClientId { get; init; }   // hub-assigned (may qualify the app self-id)
    public string? AppId { get; init; }              // app's self-reported id
    public string? DisplayName { get; init; }
    public int     ProcessId { get; init; }
    public bool    IsConnected { get; init; }
    public bool    ReadOnly { get; init; }
    public IReadOnlyList<ToolDescriptor> Tools { get; init; } = Array.Empty<ToolDescriptor>();
    public string? ExecutablePath { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
}
```

`HubMcpServer.Start(IClientBroker, HubOptions)` consumes this. A compiling
`StubClientBroker` is provided (in-memory `Upsert`/`MarkDown`/`InvokeHandler`) so the
Hub exe runs and the MCP server is testable today. **Phase-B replaces the stub** with
the live broker: `PipeTransport.CreateServerStream(ControlPipe)` accept loop → per-client
`PipeChannel` → handle `Register`/`ToolList`/`Heartbeat`/`ClientDown`, correlate
`InvokeTool`↔`ToolResult` by `MessageEnvelope.CorrelationId`, and implement
launch/restart via `ExecutablePath`.

### Hub wiring already in place
`Program.Main` (single-instance mutex) → `HubRuntime.Start(broker, options)` (starts
`HubMcpServer` + `HubPipeMcpListener`) → Avalonia desktop lifetime (`App` = scaffold
status window; Phase-B = tray UI + audit log + read-only toggles + "AI driving X").

---

## 6. End-to-end wire flow (for reference)

```
AI MCP client → spawns keincheck-connect (stdio)
  └─ ensures hub up (mutex) → connects hub MCP-over-pipe → byte-pumps stdio<->pipe
Hub (single MCP server): meta-tools + proxy of ACTIVE client's tools (verbatim schemas)
  └─ tools/call → IClientBroker.InvokeOnClientAsync → InvokeTool over ControlPipe
App (embeds Keincheck.Client, NO ASP.NET):
  └─ Register + ToolList(Describe()) → receive InvokeTool → ClientToolHost.InvokeAsync
     (Core tool on UI thread) → ToolResult(content blocks) → hub → AI
```

Proven in `Stage2SpikeTests`: #1 build+schema+invoke; #2 dynamic list/call over a real
MCP session; #3 a real `McpClient` round-trips initialize/list/call through the hub's MCP
server over an in-memory duplex stream (the named-pipe substitute).
