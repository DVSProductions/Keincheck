using System.Linq;
using System.Text.Json;
using Keincheck.Avalonia;
using Keincheck.Client;
using Keincheck.Core;
using Keincheck.Core.Tools;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Tests for finding-5: <c>set_property</c> and <c>automation_action</c> (and, for
/// consistency, <c>set_focus</c>) must NOT force the caller to pass the unused
/// handle/selector discriminator. ModelContextProtocol marks any parameter without a
/// compile-time default as schema-<c>required</c>, so the fix reorders the genuinely
/// required parameters (<c>propertyName</c> + the <see cref="JsonElement"/> <c>value</c>)
/// to the front and defaults <c>handle</c>/<c>selector</c> to <c>null</c>.
/// </summary>
/// <remarks>
/// The schema assertions read the ACTUAL MCP input-schema the SDK generates for each
/// tool (via <see cref="ClientToolHost.Describe"/>, the same projection the hub
/// re-advertises), so they prove what an MCP client really sees. The behavioural tests
/// drive the real tool methods through the neutral seam against the headless window,
/// addressing the control by ONLY a selector (then ONLY a handle) to prove neither
/// discriminator is mandatory at the call site.
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class ActionToolsSchemaUiTests
{
    private readonly HeadlessSession _session;

    public ActionToolsSchemaUiTests(HeadlessSession session) => _session = session;

    private static IUiAdapter NewAdapter() => new AvaloniaUiAdapter(new PropertyValueSerializer(8));
    private static IUiDispatcher NewDispatcher() => new AvaloniaUiDispatcher();

    /// <summary>Reads the generated MCP input schema for one Core tool by its protocol name.</summary>
    private JsonElement SchemaFor(string toolName)
    {
        // The Application must exist for the host's DI/tool build; touch it on the UI thread.
        _ = _session.RunOnUiThread(() => global::Avalonia.Application.Current!);
        using var host = ClientToolHost.Build(NewAdapter(), NewDispatcher(), new McpServerOptions());
        var descriptor = host.Describe().Single(d => d.Name == toolName);
        Assert.NotNull(descriptor.InputSchema);
        return descriptor.InputSchema!.Value.Clone();
    }

    /// <summary>The names in a JSON Schema "required" array (empty when the key is absent).</summary>
    private static HashSet<string> RequiredOf(JsonElement schema)
    {
        if (!schema.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.Array)
            return new HashSet<string>(StringComparer.Ordinal);
        return req.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    // ---- schema-required shape (finding-5 core assertion) -----------------

    [Fact]
    public void SetProperty_Schema_Requires_PropertyName_And_Value_But_Not_Handle_Or_Selector()
    {
        var schema = SchemaFor("set_property");
        var required = RequiredOf(schema);

        // The two genuinely-required parameters must be required...
        Assert.Contains("propertyName", required);
        Assert.Contains("value", required);
        // ...and the discriminators must be OPTIONAL (the whole point of the fix).
        Assert.DoesNotContain("handle", required);
        Assert.DoesNotContain("selector", required);

        // All four still exist as declared properties; only their required-ness changed.
        var props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty("propertyName", out _));
        Assert.True(props.TryGetProperty("value", out _));
        Assert.True(props.TryGetProperty("handle", out _));
        Assert.True(props.TryGetProperty("selector", out _));
    }

    [Fact]
    public void AutomationAction_Schema_Requires_Neither_Handle_Nor_Selector()
    {
        var schema = SchemaFor("automation_action");
        var required = RequiredOf(schema);

        // automation_action has no genuinely-required parameter (action/value default),
        // so handle and selector must both be optional.
        Assert.DoesNotContain("handle", required);
        Assert.DoesNotContain("selector", required);

        var props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty("handle", out _));
        Assert.True(props.TryGetProperty("selector", out _));
        Assert.True(props.TryGetProperty("action", out _));
    }

    [Fact]
    public void SetFocus_Schema_Requires_Neither_Handle_Nor_Selector()
    {
        var schema = SchemaFor("set_focus");
        var required = RequiredOf(schema);

        Assert.DoesNotContain("handle", required);
        Assert.DoesNotContain("selector", required);
    }

    // ---- behavioural: only-selector and only-handle both succeed ----------

    [Fact]
    public void SetProperty_With_Only_Selector_Is_Callable_And_Returns_Structured_Result()
    {
        // A selector-only call (no handle argument at all) must bind and return a structured
        // result. NOTE: ActionTools.Resolve routes a selector through registry.Query with NULL
        // scope, which enumerates top-levels via the desktop lifetime the shared headless
        // session deliberately does not install (see SpineUiTests.Query_With_Null_Scope_*), so
        // the selector resolves to no match HERE — successful selector resolution against a real
        // rooted tree is covered by ClassSelectorUiTests/NeutralSeamUiTests (scoped Query). The
        // point under test is that the discriminator is genuinely optional: omitting handle is a
        // valid call that yields structured output rather than a missing-argument failure.
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();

            var value = JsonDocument.Parse("123").RootElement;
            var result = ActionTools
                .SetProperty(registry, ui, dispatcher, "Width", value, selector: "#Save")
                .GetAwaiter().GetResult();
            return JsonSerializer.Serialize(result);
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // It resolved the selector path (not the "provide a handle or selector" guard): a
        // selector-only call is accepted, and the result is a structured object with an 'ok' flag.
        Assert.Equal(JsonValueKind.False, root.GetProperty("ok").ValueKind);
        Assert.Contains("#Save", root.GetProperty("error").GetString());
    }

    [Fact]
    public void SetProperty_With_Only_Handle_Succeeds()
    {
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();

            _ = TestWindowFactory.Create(out var saveButton, out _);
            var handle = registry.Assign(saveButton);

            var value = JsonDocument.Parse("77").RootElement;
            // No selector argument at all — addressed purely by handle.
            var result = ActionTools
                .SetProperty(registry, ui, dispatcher, "Width", value, handle: handle)
                .GetAwaiter().GetResult();
            return JsonSerializer.Serialize(result);
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(77d, root.GetProperty("newValue").GetDouble());
    }

    [Fact]
    public void SetFocus_With_Only_Handle_Succeeds()
    {
        // set_focus is selector/handle-symmetric after the fix; the handle path works in the
        // headless harness (no desktop-lifetime null-scope dependency), so it proves a
        // single-discriminator call succeeds and returns the focus result fields.
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();

            _ = TestWindowFactory.Create(out _, out var inputBox);
            var handle = registry.Assign(inputBox);

            var result = ActionTools
                .SetFocus(registry, ui, dispatcher, handle: handle)
                .GetAwaiter().GetResult();
            return JsonSerializer.Serialize(result);
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.TryGetProperty("focusRequested", out _));
    }

    // ---- regression: omitting BOTH still returns the structured Resolve error -

    [Fact]
    public void SetProperty_With_Neither_Handle_Nor_Selector_Returns_Structured_Error()
    {
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();

            var value = JsonDocument.Parse("10").RootElement;
            var result = ActionTools
                .SetProperty(registry, ui, dispatcher, "Width", value)
                .GetAwaiter().GetResult();
            return JsonSerializer.Serialize(result);
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("error").GetString()));
    }

    [Fact]
    public void AutomationAction_With_Neither_Handle_Nor_Selector_Returns_Structured_Error()
    {
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();

            var result = ActionTools
                .AutomationAction(registry, ui, dispatcher)
                .GetAwaiter().GetResult();
            return JsonSerializer.Serialize(result);
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("error").GetString()));
    }
}
