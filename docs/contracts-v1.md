# AvaloniaMcp — Shared Spine Contracts

This file is the **ground truth** for module agents. Every signature below is
copied verbatim from the built, green library (`AvaloniaMcp`, `net8.0`). Code
your tool classes against these exact symbols. If you need a new spine member,
ask the Foundation agent — do not change these signatures unilaterally.

- Namespace for everything below: **`AvaloniaMcp`**
- Library target framework: **net8.0** (consumed by net10 Demo/Tests via `RollForward=Major`)
- MCP packages: **ModelContextProtocol 1.4.0**, **ModelContextProtocol.AspNetCore 1.4.0**
- Avalonia: **12.0.4** (the `Avalonia` metapackage)

---

## McpServerOptions

```csharp
namespace AvaloniaMcp;

public sealed class McpServerOptions
{
    public int  Port                  { get; set; } = 3001;   // Kestrel port, bound to 127.0.0.1 only
    public int  MaxScreenshotDimension { get; set; } = 2048;  // max width/height of a captured PNG
    public int  MaxSerializationDepth { get; set; } = 25;     // recursion cap for tree/property serialization
    public int  BindingErrorBufferSize { get; set; } = 256;   // ring buffer size for BindingErrorSink
    public bool CaptureBindingErrors  { get; set; } = true;   // install BindingErrorSink on host start
}
```

Registered as a **DI singleton** by `McpHost`. Inject it into tool methods via a
constructor/method parameter of type `McpServerOptions`.

---

## UiDispatch

```csharp
namespace AvaloniaMcp;

public static class UiDispatch
{
    // Marshal a function onto the UI thread and get its result.
    public static Task<T> Run<T>(Func<T> fn);

    // Marshal an action onto the UI thread.
    public static Task Run(Action action);

    // Marshal an async function onto the UI thread (inner Task is unwrapped).
    public static Task<T> RunAsync<T>(Func<Task<T>> fn);
    public static Task    RunAsync(Func<Task> fn);
}
```

**Rule:** any handler that touches the visual tree, controls, rendering,
`Application.Current`, or a `TopLevel` MUST run inside one of these. If already
on the UI thread, the call executes synchronously (no deadlock).

---

## ControlRegistry

```csharp
namespace AvaloniaMcp;

public sealed class ControlRegistry
{
    // Assign (or return existing) stable handle, e.g. "ctl-1a". Idempotent per control.
    public string Assign(Control control);

    // Resolve a handle to a live control. false if unknown/collected (prunes dead).
    public bool TryResolve(string id, out Control? control);

    // Evaluate a CSS-ish selector. scope == null searches all open TopLevels.
    // Never throws on a bad selector — returns an empty list.
    public IReadOnlyList<Control> Query(string selector, TopLevel? scope = null);

    // All open top-level visuals (windows / single-view root). UI-thread only.
    public static IEnumerable<Visual> EnumerateRoots();
}
```

Registered as a **DI singleton** by `McpHost`. Inject `ControlRegistry` into tool
methods. Holds **weak references** — assigning a handle never keeps a control alive.

### Selector grammar (supported by `Query`)

Whitespace-separated chain of simple selectors joined by combinators:

| Form                | Meaning                                                            |
|---------------------|-------------------------------------------------------------------|
| `Type`              | control whose runtime type **or any base type** is named `Type`   |
| `Type[Name=x]`      | `Type` whose `Name` attribute equals `x`                          |
| `#Name`             | any control whose `Name` equals `Name` (sugar for `[Name=Name]`)  |
| `[Name=x]`          | standalone attribute predicate (any type)                         |
| `[Prop=val]`        | any public CLR string-comparable property equals `val`            |
| `A B`               | descendant: `B` anywhere under `A`                                |
| `A > B`             | child: `B` is a direct child of `A`                               |

- Attribute values may be quoted: `[Name='my value']` or `[Name="my value"]`.
- Matching walks the **merged logical + visual** subtree (template parts reachable).
- Results are de-duplicated, in document order per root.
- `Name` matching is **ordinal/case-sensitive**; type names are ordinal.

---

## PropertyValueSerializer

```csharp
namespace AvaloniaMcp;

public sealed class PropertyValueSerializer
{
    public PropertyValueSerializer(int maxDepth = 8);  // host passes McpServerOptions.MaxSerializationDepth

    // Read an Avalonia styled/attached property value -> JSON-friendly object.
    public object? Read(Control control, AvaloniaProperty property);

    // Read a CLR property by name -> JSON-friendly object (null if missing/unreadable).
    public object? Read(Control control, string propertyName);

    // Coerce a JsonElement onto a named CLR property. Structured failure, never throws.
    public bool TryWrite(Control control, string propertyName, JsonElement value, out string error);

    // Static coercion primitive used by TryWrite (and reusable by tools).
    public static bool TryCoerce(JsonElement value, Type targetType, out object? result, out string error);
}
```

Registered as a **DI singleton** by `McpHost`. Coercion order in `TryCoerce`:
1. `null` handling (honors nullable targets)
2. `string`, `bool`, `enum` (case-insensitive), numeric primitives
3. `System.ComponentModel.TypeConverter.ConvertFromInvariantString`
4. **static `Parse(string)` / `Parse(string, IFormatProvider)`** — covers Avalonia
   structs like `Thickness`, `Color`, `GridLength`, `Point`, `CornerRadius`
5. assignable-from-string fallback

`Read` projects complex values to JSON-friendly forms: primitives pass through,
enums/Avalonia value-structs become their string form, controls become
`"TypeName#Name"`, `IEnumerable` becomes a capped list (≤50 items), depth-limited.

---

## BindingErrorSink

```csharp
namespace AvaloniaMcp;

public sealed class BindingErrorSink : Avalonia.Logging.ILogSink
{
    public BindingErrorSink(int capacity = 256, ILogSink? inner = null, bool bindingOnly = true);

    public static BindingErrorSink? Current { get; }                 // currently-installed instance, if any
    public static BindingErrorSink  Install(int capacity = 256, bool bindingOnly = true); // swaps Logger.Sink, chains prior
    public void Uninstall();                                         // restores the chained inner sink

    // ILogSink:
    public bool IsEnabled(LogEventLevel level, string area);
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate);
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues);

    public IEnumerable<string> Recent(int n);  // up to n most-recent, OLDEST first; n<=0 => all
    public void Clear();
}
```

The host installs this (when `CaptureBindingErrors` is true) and registers the
instance as a **DI singleton**. A diagnostics tool can inject `BindingErrorSink`
and call `Recent(n)`. Filters on `LogArea.Binding` by default; always forwards to
the previously-installed sink.

---

## McpHost

```csharp
namespace AvaloniaMcp;

public static class McpHost
{
    // Builds a Kestrel WebApplication on 127.0.0.1:opts.Port on a BACKGROUND thread,
    // registers the spine as DI singletons, AddMcpServer().WithHttpTransport()
    // .WithToolsFromAssembly(...), MapMcp(). Dispose() stops Kestrel + restores the sink.
    public static IDisposable Attach(Application app, McpServerOptions opts);
}
```

DI singletons registered by the host (available to tool methods as parameters):
`Application`, `McpServerOptions`, `ControlRegistry`, `PropertyValueSerializer`,
and `BindingErrorSink` (only when `CaptureBindingErrors` is true).

Tools are discovered via `WithToolsFromAssembly` from **both** the `AvaloniaMcp`
library assembly **and** the entry assembly, so tool classes may live in either.

---

## AppBuilderExtensions

```csharp
namespace AvaloniaMcp;

public static class AppBuilderExtensions
{
    // Wire into Program.cs: AppBuilder.Configure<App>().UsePlatformDetect().UseMcpServer();
    public static AppBuilder UseMcpServer(this AppBuilder builder, Action<McpServerOptions>? configure = null);
}
```

Starts the host in `AppBuilder.AfterSetup` (UI thread). Shutdown is wired to the
desktop lifetime's `Exit` event (deferred onto the dispatcher if the lifetime
isn't assigned yet).

---

## Tool-class convention (module agents)

Create one `[McpServerToolType]` **static** class per module under
`AvaloniaMcp/Tools/`. Methods are `[McpServerTool]` + `[Description("…")]`.
Dependencies arrive via DI **method parameters**.

```csharp
using System.ComponentModel;                 // [Description]
using ModelContextProtocol.Server;           // [McpServerToolType], [McpServerTool]
using AvaloniaMcp;

namespace AvaloniaMcp.Tools;

[McpServerToolType]
public static class ExampleTools
{
    [McpServerTool, Description("Resolve a selector and return matching control handles.")]
    public static async Task<object> FindControls(
        ControlRegistry registry,                       // DI singleton
        PropertyValueSerializer serializer,             // DI singleton
        [Description("CSS-ish selector, e.g. \"Button[Name=ok]\".")] string selector)
    {
        // Marshal ALL visual-tree access onto the UI thread.
        return await UiDispatch.Run(() =>
        {
            var matches = registry.Query(selector);     // null scope => all TopLevels
            var handles = matches.Select(registry.Assign).ToArray();
            return (object)new { count = handles.Length, handles };
        });
    }
}
```

### Hard rules for tool methods
1. **Attributes:** class `[McpServerToolType]`; method `[McpServerTool]` + `[Description]`.
   Both attributes are in namespace `ModelContextProtocol.Server`
   (assembly `ModelContextProtocol.Core`). `[Description]` is `System.ComponentModel`.
2. **Threading:** every access to controls / visual tree / rendering /
   `Application.Current` / `TopLevel` goes through `UiDispatch.Run(...)`.
3. **DI:** request `ControlRegistry`, `PropertyValueSerializer`, `McpServerOptions`,
   `BindingErrorSink`, or `Application` as method parameters. Do not new them up.
4. **Errors:** never throw raw on a bad handle/selector. Return a structured
   result object describing the failure (e.g. `new { ok = false, error = "..." }`).
5. **Return values:** must be JSON-serializable (anonymous objects, records,
   primitives, arrays). Use `registry.Assign(control)` to return stable handles.
6. **Addressing:** accept either a handle (`registry.TryResolve`) or a selector
   (`registry.Query`). A common pattern is "handle if it resolves, else selector".

### Verified MCP 1.4.0 host API (already wired in McpHost — for reference)
```csharp
builder.Services
    .AddMcpServer()                       // Microsoft.Extensions.DependencyInjection
    .WithHttpTransport()                  // Microsoft.Extensions.DependencyInjection (AspNetCore pkg)
    .WithToolsFromAssembly(assembly);     // Microsoft.Extensions.DependencyInjection
app.MapMcp();                             // Microsoft.AspNetCore.Builder, returns IEndpointConventionBuilder
```

### Verified Avalonia 12.0.4 APIs for module agents
- Screenshots: `new RenderTargetBitmap(new PixelSize(w, h))`, `.Render(visual)`,
  `.Save(Stream)` (PNG).
- Automation: `Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(control)`;
  provider interfaces in `Avalonia.Automation.Provider`:
  `IInvokeProvider`, `IToggleProvider`, `IValueProvider`, `IExpandCollapseProvider`,
  `ISelectionItemProvider`, `IRangeValueProvider`, `IScrollProvider`, `ISelectionProvider`.
- TopLevel: `TopLevel.GetTopLevel(visual)`.
