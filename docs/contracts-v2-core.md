# AvaloniaMcp v2 — Stage 1 Substrate Contracts

This file is the **ground truth** for Phase-B and Stage-2 agents. Every signature
below is copied verbatim from the **built, green** solution (5 projects, 24 tests
pass / 1 skip). Code against these exact symbols. If you need a new member, ask
the Foundation agent — do not change these signatures unilaterally.

## Assemblies, targets, dependencies

| Assembly                 | TFM     | References                                                                 |
|--------------------------|---------|----------------------------------------------------------------------------|
| **AvaloniaMcp.Protocol** | net8.0  | **none** (BCL only — zero deps; this is the wire contract)                  |
| **AvaloniaMcp.Core**     | net8.0  | Avalonia 12.0.4, ModelContextProtocol 1.4.0 (**core only**), → Protocol     |
| **AvaloniaMcp**          | net8.0  | ModelContextProtocol.AspNetCore 1.4.0, FrameworkReference AspNetCore, → Core |
| **AvaloniaMcp.Demo**     | net10.0 | Avalonia.Desktop/Fluent, → AvaloniaMcp                                       |
| **AvaloniaMcp.Tests**    | net10.0 | Avalonia.Headless + xunit, → AvaloniaMcp, → Core, → Protocol                 |

- net10 consumers reference net8 libs via `<RollForward>Major</RollForward>`.
- **Core does NOT reference** ModelContextProtocol.AspNetCore or the ASP.NET Core
  shared framework. Hosting stays in **AvaloniaMcp**.

## Namespace map (what moved in Stage 1)

| Type                                                       | v1 namespace        | v2 namespace (now)        |
|------------------------------------------------------------|---------------------|---------------------------|
| `ControlRegistry`, `PropertyValueSerializer`, `UiDispatch` | `AvaloniaMcp`       | **`AvaloniaMcp.Core`**    |
| `BindingErrorSink`, `McpServerOptions`                     | `AvaloniaMcp`       | **`AvaloniaMcp.Core`**    |
| `SelectorChain`, `SimpleSelector` (internal)               | `AvaloniaMcp`       | **`AvaloniaMcp.Core`**    |
| `IUiAdapter`, `AvaloniaUiAdapter` (NEW)                    | —                   | **`AvaloniaMcp.Core`**    |
| `InspectionTools`/`ScreenshotTools`/`ActionTools`/`InputTools` | `AvaloniaMcp.Tools` | **`AvaloniaMcp.Core.Tools`** |
| `McpHost`, `AppBuilderExtensions`                          | `AvaloniaMcp`       | **`AvaloniaMcp`** (unchanged) |

The **public `UseMcpServer` API is unchanged** (still in namespace `AvaloniaMcp`),
so the ProtoFace app that calls `.UseMcpServer()` still compiles untouched.

The 22 `[McpServerTool]` methods now live in the **Core assembly**, so
`McpHost` discovers them via `WithToolsFromAssembly(typeof(InspectionTools).Assembly)`.

---

## 1. AvaloniaMcp.Protocol (zero-dependency wire substrate)

### ProtocolVersion (handshake)

```csharp
namespace AvaloniaMcp.Protocol;

public static class ProtocolVersion
{
    public const int    Current = 1;        // bump on incompatible change
    public const int    Minimum = 1;        // lowest version still spoken
    public const string Magic   = "AMCP";   // 4-byte channel sanity token
    public static bool IsCompatible(int version);   // Minimum <= version <= Current
}
```

### FrameCodec (length-prefixed, CHUNKED framing over a Stream)

```csharp
namespace AvaloniaMcp.Protocol;

public static class FrameCodec
{
    public const int ChunkHeaderSize       = 9;             // 4 magic + 1 flags + 4 length(BE uint)
    public const int DefaultMaxChunkPayload = 64 * 1024;    // bytes per wire chunk
    public const int DefaultMaxMessageSize  = 32 * 1024 * 1024; // reassembly cap (fits PNGs)

    // Write a logical message as one-or-more chunks (splits at maxChunkPayload), then Flush.
    public static void Write(Stream stream, ReadOnlySpan<byte> payload,
                             int maxChunkPayload = DefaultMaxChunkPayload);
    public static Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload,
                             int maxChunkPayload = DefaultMaxChunkPayload,
                             CancellationToken cancellationToken = default);

    // Read ONE reassembled message (all chunks up to & incl. the final chunk).
    // Returns null on a clean EOF *between* frames; throws ProtocolException on a
    // truncated frame, magic mismatch, or over-cap chunk/message.
    public static Task<byte[]?> TryReadAsync(Stream stream,
                             int maxChunkPayload = DefaultMaxChunkPayload,
                             int maxMessageSize  = DefaultMaxMessageSize,
                             CancellationToken cancellationToken = default);
}

public sealed class ProtocolException : Exception { /* ctors */ }
```

**Wire format per chunk:** `[4] magic "AMCP"` · `[1] flags (bit0 = final)` ·
`[4] BE uint chunk length N` · `[N] payload bytes`. A frame = chunks until the
`final` flag; an empty message is one final chunk with `N == 0`.

### Messages (IPC DTOs) + envelope

```csharp
namespace AvaloniaMcp.Protocol;

public enum MessageKind { Unknown=0, Register=1, Heartbeat=2, ToolList=3,
                          InvokeTool=4, ToolResult=5, ClientDown=6 }

public sealed class MessageEnvelope            // the outer wrapper of every message
{
    public MessageKind Kind          { get; set; }   // "kind"
    public int         Version       { get; set; }   // "v"     (defaults ProtocolVersion.Current)
    public string?     CorrelationId { get; set; }   // "id"    (request/response link)
    public JsonElement Payload       { get; set; }   // "payload" (concrete DTO as JSON)

    public static MessageEnvelope Wrap<T>(MessageKind kind, T message,
        string? correlationId = null, JsonSerializerOptions? options = null);
    public T? Unwrap<T>(JsonSerializerOptions? options = null);
}

public sealed class RegisterMessage   { string ClientId; string? DisplayName; int ProcessId; int ProtocolVersion; }
public sealed class HeartbeatMessage  { string ClientId; long Sequence; long TimestampUnixMs; }
public sealed class ToolDescriptor    { string Name; string? Description; JsonElement? InputSchema; }
public sealed class ToolListMessage   { string ClientId; IReadOnlyList<ToolDescriptor> Tools; }
public sealed class InvokeToolMessage { string ClientId; string ToolName; JsonElement? Arguments; }
public sealed class ToolResultMessage { string ClientId; string ToolName; bool IsError; JsonElement? Content; string? Error; }
public sealed class ClientDownMessage { string ClientId; string? Reason; bool Graceful; }

public static class ProtocolJson { public static readonly JsonSerializerOptions Options; }
// Web defaults + JsonStringEnumConverter + ignore-null. Use for ALL message (de)serialization.
```

**Usage:** `Wrap` a DTO → `JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options)`
→ `FrameCodec.WriteAsync`. Receiver: `FrameCodec.TryReadAsync` → deserialize the
`MessageEnvelope` → switch on `Kind` → `envelope.Unwrap<TConcrete>()`.

---

## 2. AvaloniaMcp.Core — IUiAdapter (the framework-agnostic seam)

Phase B reroutes **every** tool body through this interface. It is registered as a
DI singleton by `McpHost` (`AddSingleton<IUiAdapter>(new AvaloniaUiAdapter(...))`),
so a tool method can request `IUiAdapter` as a parameter today. **Threading:** all
members are UI-thread-affine (call inside `UiDispatch.Run`); the adapter does not
re-marshal.

```csharp
namespace AvaloniaMcp.Core;

public interface IUiAdapter
{
    // -- topology --
    IEnumerable<Visual>  EnumerateRoots();
    TopLevel?            GetTopLevel(Visual visual);
    IEnumerable<Control> GetLogicalChildren(Control control);
    IEnumerable<Control> GetVisualChildren(Control control);

    // -- metadata --
    string  GetTypeName(Control control);
    string? GetName(Control control);
    string? GetTitle(Control control);                 // window title or null
    Rect    GetBounds(Control control);
    bool    IsEffectivelyVisible(Control control);
    bool    IsEffectivelyEnabled(Control control);
    bool    IsActiveWindow(Control control);

    // -- properties --
    IEnumerable<AvaloniaProperty> GetRegisteredProperties(Control control);
    object? ReadProperty(Control control, AvaloniaProperty property);
    object? ReadProperty(Control control, string propertyName);
    bool    WriteProperty(Control control, string propertyName, JsonElement value, out string error);

    // -- render to PNG (UI thread) --
    bool TryRenderControlToPng(Control control, int maxDimension, out byte[] png, out string error);
    bool TryRenderVisualToPng(Visual visual, int maxDimension, Rect? cropRect, out byte[] png, out string error);

    // -- UI automation --
    UiAutomationResult InvokeAutomation(Control control, UiAutomationAction action, string? value);

    // -- focus --
    bool     SetFocus(Control control);
    Control? GetFocusedElement(TopLevel topLevel);

    // -- hit-test --
    Control? HitTest(TopLevel topLevel, Point point);

    // -- synthetic input (UI thread) --
    Control? SendPointer(TopLevel topLevel, PointerAction action, Point point);  // returns hit control
    Control? SendWheel(TopLevel topLevel, Point point, Vector delta);
    Control? SendText(Control? target, string text);                             // null target => focused
    bool     SendKeys(Control? target, string chords,
                      out IReadOnlyList<string> sentChords, out Control? sink, out string error);

    // -- diagnostics --
    IReadOnlyList<string> GetRecentBindingErrors(int count, out bool enabled);   // oldest first; count<=0 => all
}

public enum UiAutomationAction { Auto=0, Invoke, Toggle, SetValue, Expand, Collapse, Select }
public enum PointerAction       { Move=0, Down, Up, Click, DoubleClick, RightClick }

public readonly record struct UiAutomationResult(bool Ok, string? Action, string? State, string? Error)
{
    public static UiAutomationResult Success(string action, string? state = null);
    public static UiAutomationResult Failure(string error);
}
```

### AvaloniaUiAdapter (the Avalonia 12 implementation)

```csharp
namespace AvaloniaMcp.Core;

public sealed class AvaloniaUiAdapter : IUiAdapter
{
    public AvaloniaUiAdapter(PropertyValueSerializer serializer,
                             BindingErrorSink? bindingErrors = null,
                             int defaultMaxScreenshotDimension = 2048);
}
```

**Coverage proof** (each of the 22 tools maps onto adapter members):
`list_windows`→EnumerateRoots/Get*; `get_logical_tree`/`get_visual_tree`→GetLogical/VisualChildren+ReadProperty;
`query_controls`→ControlRegistry.Query (+ Get* metadata); `get_properties`/`get_property`→GetRegisteredProperties/ReadProperty;
`get_data_context`→ReadProperty; `get_text`→GetLogical/VisualChildren+IsEffectivelyVisible;
`get_binding_errors`→GetRecentBindingErrors; `hit_test`→HitTest; `get_focused_element`→GetFocusedElement;
`set_property`→WriteProperty; `automation_action`→InvokeAutomation; `set_focus`→SetFocus;
`wait_for`→ReadProperty + ControlRegistry.Query; `screenshot_window`→TryRenderVisualToPng;
`screenshot_control`→TryRenderControlToPng; `pointer`/`click_at`→SendPointer; `scroll_at`→SendWheel;
`type_text`→SendText; `send_keys`→SendKeys.

> Selector/handle resolution itself stays on `ControlRegistry` (below); the adapter
> covers the per-control primitives. Tools combine the two.

---

## 3. AvaloniaMcp.Core — spine (moved verbatim, namespace `AvaloniaMcp.Core`)

Signatures are **unchanged from v1 CONTRACTS.md** — only the namespace moved from
`AvaloniaMcp` to `AvaloniaMcp.Core`. They remain DI singletons registered by `McpHost`.

```csharp
namespace AvaloniaMcp.Core;

public sealed class McpServerOptions {                      // DI singleton
    public int  Port                   { get; set; } = 3001;
    public int  MaxScreenshotDimension { get; set; } = 2048;
    public int  MaxSerializationDepth  { get; set; } = 25;
    public int  BindingErrorBufferSize { get; set; } = 256;
    public bool CaptureBindingErrors   { get; set; } = true;
}

public static class UiDispatch {                            // marshal to UI thread
    public static Task<T> Run<T>(Func<T> fn);
    public static Task    Run(Action action);
    public static Task<T> RunAsync<T>(Func<Task<T>> fn);
    public static Task    RunAsync(Func<Task> fn);
}

public sealed class ControlRegistry {                       // DI singleton; weak refs
    public string Assign(Control control);                 // idempotent "ctl-1a"
    public bool   TryResolve(string id, out Control? control);
    public IReadOnlyList<Control> Query(string selector, TopLevel? scope = null); // never throws
    public static IEnumerable<Visual> EnumerateRoots();    // UI-thread only
}

public sealed class PropertyValueSerializer {              // DI singleton
    public PropertyValueSerializer(int maxDepth = 8);
    public object? Read(Control control, AvaloniaProperty property);
    public object? Read(Control control, string propertyName);
    public bool    TryWrite(Control control, string propertyName, JsonElement value, out string error);
    public static bool TryCoerce(JsonElement value, Type targetType, out object? result, out string error);
}

public sealed class BindingErrorSink : Avalonia.Logging.ILogSink {   // DI singleton (when capture on)
    public BindingErrorSink(int capacity = 256, ILogSink? inner = null, bool bindingOnly = true);
    public static BindingErrorSink? Current { get; }
    public static BindingErrorSink  Install(int capacity = 256, bool bindingOnly = true);
    public void Uninstall();
    public IEnumerable<string> Recent(int n);              // oldest first; n<=0 => all
    public void Clear();
}
```

Selector grammar is unchanged from v1 (`Type`, `Type[Name=x]`, `#Name`, `[Prop=val]`,
`A B` descendant, `A > B` child; ordinal/case-sensitive; merged logical+visual walk).

### Two hard-won fixes preserved (do NOT regress)
1. The merged logical+visual walks carry a shared `HashSet<Visual>` visited-guard
   (`SelectorChain.Descendants` and `InspectionTools.CollectText`) — overlay/popup/
   adorner cross-links would otherwise StackOverflow the host.
2. No `[McpServerTool]` parameter is a `JsonElement` **with a default value**; optional
   JSON params are `JsonElement? x = null` (a `JsonElement = default` crashes MapMcp() schema-gen).

---

## 4. AvaloniaMcp — host (public API UNCHANGED, namespace `AvaloniaMcp`)

```csharp
namespace AvaloniaMcp;

public static class AppBuilderExtensions {
    // UNCHANGED public surface — ProtoFace's .UseMcpServer() still compiles.
    public static AppBuilder UseMcpServer(this AppBuilder builder,
                                          Action<McpServerOptions>? configure = null);
}

public static class McpHost {
    public static IDisposable Attach(Application app, McpServerOptions opts);
}
```

`McpHost` DI singletons available to tool methods as parameters:
`Application`, `McpServerOptions`, `ControlRegistry`, `PropertyValueSerializer`,
`BindingErrorSink` (when `CaptureBindingErrors`), and **`IUiAdapter`** (new).
Tool discovery now scans the **Core** assembly (`typeof(InspectionTools).Assembly`)
plus the entry assembly.

---

## Phase-B handoff

- Reroute each tool body to call `IUiAdapter` (+ `ControlRegistry` for resolution)
  instead of Avalonia directly. The interface above is **complete** for all 22 tools.
- After rerouting, delete the now-duplicate `private static class SyntheticInput`
  nested in `Core/Tools/InputTools.cs` (the shared `AvaloniaMcp.Core.SyntheticInput`
  the adapter uses replaces it).
- Do not change `IUiAdapter`'s signatures; add members only via the Foundation agent.
```
