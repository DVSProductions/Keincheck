using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Keincheck.Avalonia;
using Keincheck.Core;
using Keincheck.Core.Tools;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// End-to-end tests for the Phase-A/C accessibility (semantic) tool surface
/// (<c>get_semantic_tree</c>, <c>screenshot_marked</c>, <c>describe_screen</c>,
/// <c>wait_for_idle</c>) driven against the real <see cref="AvaloniaUiAdapter"/> and
/// <see cref="AvaloniaUiDispatcher"/> on the shared headless UI thread — exactly how the
/// live host wires them. The tools are invoked through the neutral seam: the adapter is
/// referenced only via <see cref="IUiAdapter"/>/<see cref="IUiDispatcher"/>, element
/// handles stay opaque, and every assertion goes through the tool's structured result.
/// </summary>
/// <remarks>
/// <para>
/// The window is constructed with the shared <c>TestWindowFactory</c> and shown so it is
/// laid out and renderable. Tools are targeted by the window's <b>handle</b> (assigned via
/// the same <see cref="ControlRegistry"/> the tools use), never by null-scope root
/// enumeration — the headless session installs no desktop lifetime, so a null-scope walk
/// would deadlock (see <c>SpineUiTests.Query_With_Null_Scope_*</c>). Scoping by handle
/// exercises the full <c>TryResolveTopLevel</c> → semantic-walk → render path.
/// </para>
/// <para>
/// These exercise the Avalonia overrides of the three new seam methods
/// (<see cref="IUiAdapter.GetSemanticInfo"/>, <see cref="IUiAdapter.TryGetBoundsInTopLevel"/>,
/// <see cref="IUiAdapter.TryRenderAnnotated"/>): the Save <see cref="Avalonia.Controls.Button"/>
/// and Input <see cref="Avalonia.Controls.TextBox"/> are interactive and yield a real
/// on-screen box, so they appear as semantic nodes and as numbered marks.
/// </para>
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class SemanticToolsUiTests
{
    private readonly HeadlessSession _session;

    public SemanticToolsUiTests(HeadlessSession session) => _session = session;

    // Build the real adapter/dispatcher but only ever touch them through the neutral seam.
    private static IUiAdapter NewAdapter() => new AvaloniaUiAdapter(new PropertyValueSerializer(8));
    private static IUiDispatcher NewDispatcher() => new AvaloniaUiDispatcher();
    private static McpServerOptions NewOptions() => new() { MaxScreenshotDimension = 512 };

    /// <summary>
    /// Shows the test window and drives a headless render-timer tick so the visual tree is
    /// measured/arranged — without a layout pass the controls have zero bounds, so
    /// <c>TryGetBoundsInTopLevel</c> yields no usable on-screen box and nothing gets marked.
    /// Must be called on the UI thread. The render scale is pinned so geometry is stable.
    /// </summary>
    private static Window ShowAndLayout(Window window)
    {
        window.Show();
        window.SetRenderScaling(1.0);
        // A couple of ticks settle measure -> arrange -> render so bounds are populated.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        return window;
    }

    // ---- get_semantic_tree -------------------------------------------------

    [Fact]
    public void GetSemanticTree_Surfaces_Interactive_Roles_For_Button_And_TextBox()
    {
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();

            var window = TestWindowFactory.Create(out _, out _);
            window.Show();
            try
            {
                var handle = registry.Assign(window);

                // Drive the actual MCP tool through the seam; root the walk at the window
                // handle so we never touch null-scope root enumeration.
                var result = SemanticTools
                    .GetSemanticTree(registry, ui, dispatcher, handle: handle)
                    .GetAwaiter().GetResult();

                return JsonSerializer.Serialize(result);
            }
            finally { window.Close(); }
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("rootCount").GetInt32());
        Assert.True(root.GetProperty("returned").GetInt32() >= 1);

        // The Save button and Input text box must both surface as semantic nodes with a
        // role, and both must be reported interactive by the Avalonia automation peers.
        var roles = CollectStrings(root.GetProperty("nodes"), "role");
        var interactiveCount = CountInteractive(root.GetProperty("nodes"));

        Assert.Contains("Button", roles);
        Assert.Contains(roles, r => r == "Edit" || r == "TextBox"); // TextBox peer role is "Edit"
        Assert.True(interactiveCount >= 2, $"expected >=2 interactive nodes, got {interactiveCount}");
    }

    [Fact]
    public void GetSemanticTree_InteractiveOnly_Emits_Only_Actionable_Controls()
    {
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();

            var window = TestWindowFactory.Create(out _, out _);
            window.Show();
            try
            {
                var handle = registry.Assign(window);
                var result = SemanticTools
                    .GetSemanticTree(registry, ui, dispatcher, handle: handle, interactiveOnly: true)
                    .GetAwaiter().GetResult();
                return JsonSerializer.Serialize(result);
            }
            finally { window.Close(); }
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("interactiveOnly").GetBoolean());

        // Every EMITTED leaf node carries interactive=true (non-interactive containers are
        // hoisted away). The Window/StackPanel chrome must not appear as an emitted node.
        AssertEveryEmittedNodeIsInteractive(root.GetProperty("nodes"));
    }

    // ---- screenshot_marked -------------------------------------------------

    [Fact]
    public void ScreenshotMarked_Returns_Image_And_Legend_Mapping_Marks_To_Handles()
    {
        var (mimeType, legendJson) = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();
            var options = NewOptions();

            var window = ShowAndLayout(TestWindowFactory.Create(out _, out _));
            try
            {
                var handle = registry.Assign(window);
                var result = SemanticTools
                    .ScreenshotMarked(registry, ui, dispatcher, options, target: handle)
                    .GetAwaiter().GetResult();

                // The render succeeded (not the structured-error path) and produced TWO
                // content blocks: the annotated image, then the legend text. (We assert the
                // image is a PNG content block, not its byte length: the headless drawing
                // backend encodes an empty surface, so raw bytes are environment-dependent;
                // the mark/legend LOGIC under test is fully exercised by the legend below.)
                Assert.False(result.IsError ?? false);
                Assert.Equal(2, result.Content.Count);

                var img = Assert.IsType<ImageContentBlock>(result.Content[0]);
                var legend = Assert.IsType<TextContentBlock>(result.Content[1]);
                return (img.MimeType, legend.Text);
            }
            finally { window.Close(); }
        });

        Assert.Equal("image/png", mimeType);

        using var doc = JsonDocument.Parse(legendJson);
        var legend = doc.RootElement;
        Assert.True(legend.GetProperty("ok").GetBoolean());
        var marks = legend.GetProperty("marks");

        // At least the interactive Save Button must be marked. (In this bare, theme-less
        // headless app the TextBox arranges to zero HEIGHT, so it has no usable on-screen
        // box and is correctly NOT marked — exactly the visible-box gate CollectMarkable
        // enforces. We therefore assert >=1 and pin the assertions on the Button.)
        Assert.True(marks.GetArrayLength() >= 1, "expected at least the Save Button to be marked");

        // Each legend entry maps a 1-based, contiguous mark number to a usable handle, role,
        // and a positive on-screen box.
        var seenRoles = new List<string>();
        var expectedMark = 1;
        foreach (var m in marks.EnumerateArray())
        {
            Assert.Equal(expectedMark++, m.GetProperty("mark").GetInt32());
            var id = m.GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(id));
            var b = m.GetProperty("bounds");
            Assert.True(b.GetProperty("width").GetDouble() > 0);
            Assert.True(b.GetProperty("height").GetDouble() > 0);
            if (m.GetProperty("role").GetString() is { } role)
                seenRoles.Add(role);
        }

        Assert.Contains("Button", seenRoles);
    }

    // ---- describe_screen ---------------------------------------------------

    [Fact]
    public void DescribeScreen_Bundles_Marks_Legend_And_Semantic_Summary()
    {
        var (mimeType, summaryJson) = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();
            var options = NewOptions();

            var window = ShowAndLayout(TestWindowFactory.Create(out _, out _));
            try
            {
                var handle = registry.Assign(window);
                var result = SemanticTools
                    .DescribeScreen(registry, ui, dispatcher, options, target: handle)
                    .GetAwaiter().GetResult();

                Assert.False(result.IsError ?? false);
                Assert.Equal(2, result.Content.Count);

                var img = Assert.IsType<ImageContentBlock>(result.Content[0]);
                var summary = Assert.IsType<TextContentBlock>(result.Content[1]);
                return (img.MimeType, summary.Text);
            }
            finally { window.Close(); }
        });

        Assert.Equal("image/png", mimeType);

        using var doc = JsonDocument.Parse(summaryJson);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());

        // The bundled window descriptor names the test window.
        var window = root.GetProperty("window");
        Assert.Equal("Keincheck Test Window", window.GetProperty("title").GetString());

        // Marks (at least the Save Button; see ScreenshotMarked test for why the TextBox is
        // not marked in this theme-less headless app) + a shallow semantic summary tree are
        // both present in the one round-trip.
        Assert.True(root.GetProperty("markCount").GetInt32() >= 1);
        Assert.True(root.GetProperty("marks").GetArrayLength() >= 1);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("semantics").ValueKind);
    }

    // ---- wait_for_idle -----------------------------------------------------

    [Fact]
    public void WaitForIdle_Reports_Idle_Within_Timeout()
    {
        // wait_for_idle awaits a Background-priority dispatcher barrier (the AvaloniaUiDispatcher
        // override). The headless dispatcher only pumps its queue while the session is actively
        // driving it, so we run the tool's async body THROUGH the session's async dispatch
        // (Func<Task<T>>): that drives the loop while the awaited barrier drains, exactly like
        // the live host where the UI thread keeps pumping while a background MCP call awaits.
        var json = _session.RunOnUiThread(async () =>
        {
            IUiDispatcher dispatcher = NewDispatcher();
            var result = await SemanticTools.WaitForIdle(dispatcher, timeoutMs: 4000);
            return JsonSerializer.Serialize(result);
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("idle").GetBoolean(), "the headless UI thread should settle well within 4s");
    }

    [Fact]
    public void ScreenshotMarked_Bad_Target_Returns_Structured_Error_Never_Throws()
    {
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();
            var options = NewOptions();

            var result = SemanticTools
                .ScreenshotMarked(registry, ui, dispatcher, options, target: "ctl-does-not-exist")
                .GetAwaiter().GetResult();

            Assert.True(result.IsError ?? false);
            var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            return block.Text;
        });

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Collects the value of <paramref name="prop"/> from every node in the tree.</summary>
    private static List<string> CollectStrings(JsonElement nodes, string prop)
    {
        var sink = new List<string>();
        Walk(nodes, n =>
        {
            if (n.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                sink.Add(v.GetString()!);
        });
        return sink;
    }

    private static int CountInteractive(JsonElement nodes)
    {
        var count = 0;
        Walk(nodes, n =>
        {
            if (n.TryGetProperty("interactive", out var v) && v.ValueKind == JsonValueKind.True)
                count++;
        });
        return count;
    }

    /// <summary>Asserts every EMITTED semantic node (one carrying interactive) is interactive.</summary>
    private static void AssertEveryEmittedNodeIsInteractive(JsonElement nodes)
    {
        Walk(nodes, n =>
        {
            // Hoist wrapper nodes ({ hoisted, children }) carry no 'interactive' field.
            if (n.TryGetProperty("interactive", out var v))
                Assert.True(v.GetBoolean(), "interactiveOnly emitted a non-interactive node");
        });
    }

    /// <summary>Pre-order walk over a semantic node array, recursing through "children".</summary>
    private static void Walk(JsonElement nodes, Action<JsonElement> visit)
    {
        if (nodes.ValueKind != JsonValueKind.Array)
            return;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
                continue;
            visit(node);
            if (node.TryGetProperty("children", out var children))
                Walk(children, visit);
        }
    }
}
