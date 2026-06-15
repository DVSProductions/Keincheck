using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Keincheck.Core;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Headless UI-thread tests for the shared spine: <see cref="ControlRegistry"/>
/// handle round-trips and selector queries, and <see cref="PropertyValueSerializer"/>
/// read/write round-trips. All visual-tree access runs on the headless UI thread
/// via <see cref="HeadlessSession.RunOnUiThread{T}"/>, mirroring how real tool
/// handlers marshal through <c>UiDispatch.Run</c>.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class SpineUiTests
{
    private readonly HeadlessSession _session;

    public SpineUiTests(HeadlessSession session) => _session = session;

    // ---- ControlRegistry: Assign / TryResolve round-trip ------------------

    [Fact]
    public void Registry_Assign_Is_Idempotent_And_Resolves_Back()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            var window = TestWindowFactory.Create(out var saveButton, out _);

            var id = registry.Assign(saveButton);
            Assert.False(string.IsNullOrWhiteSpace(id));

            // Idempotent: same control -> same handle.
            Assert.Equal(id, registry.Assign(saveButton));

            // Round-trip: handle -> the exact same control instance.
            Assert.True(registry.TryResolve(id, out var resolved));
            Assert.Same(saveButton, resolved);

            GC.KeepAlive(window);
        });
    }

    [Fact]
    public void Registry_Assigns_Distinct_Handles_To_Distinct_Controls()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            _ = TestWindowFactory.Create(out var saveButton, out var inputBox);

            var buttonId = registry.Assign(saveButton);
            var textBoxId = registry.Assign(inputBox);

            Assert.NotEqual(buttonId, textBoxId);
            Assert.True(registry.TryResolve(buttonId, out var b));
            Assert.True(registry.TryResolve(textBoxId, out var t));
            Assert.Same(saveButton, b);
            Assert.Same(inputBox, t);
        });
    }

    [Fact]
    public void Registry_TryResolve_Unknown_Handle_Returns_False()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            Assert.False(registry.TryResolve("ctl-deadbeef", out var control));
            Assert.Null(control);
        });
    }

    // ---- ControlRegistry: selector Query ----------------------------------

    [Fact]
    public void Query_By_Type_Finds_The_Button()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            var window = TestWindowFactory.Create(out var saveButton, out _);

            var matches = registry.Query("Button", scope: window);

            Assert.Contains(saveButton, matches);
        });
    }

    [Fact]
    public void Query_By_Type_And_Name_Finds_Only_That_Control()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            var window = TestWindowFactory.Create(out var saveButton, out var inputBox);

            var matches = registry.Query("Button[Name=Save]", scope: window);

            Assert.Single(matches);
            Assert.Same(saveButton, matches[0]);
            Assert.DoesNotContain(inputBox, matches);
        });
    }

    [Fact]
    public void Query_By_Name_Sugar_Matches_The_TextBox()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            var window = TestWindowFactory.Create(out _, out var inputBox);

            var matches = registry.Query("#Input", scope: window);

            Assert.Single(matches);
            Assert.Same(inputBox, matches[0]);
        });
    }

    [Fact]
    public void Query_Is_Name_Case_Sensitive()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            var window = TestWindowFactory.Create(out _, out _);

            // Contract: Name matching is ordinal/case-sensitive.
            Assert.Empty(registry.Query("#save", scope: window));
            Assert.Empty(registry.Query("Button[Name=save]", scope: window));
        });
    }

    [Fact]
    public void Query_Bad_Selector_Returns_Empty_Never_Throws()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            var window = TestWindowFactory.Create(out _, out _);

            // A malformed selector must yield an empty result, not an exception.
            Assert.Empty(registry.Query("Button[", scope: window));
            Assert.Empty(registry.Query("   ", scope: window));
        });
    }

    // Null-scope enumeration reads IClassicDesktopStyleApplicationLifetime.Windows
    // (ControlRegistry.EnumerateRoots) — the real desktop-app path. The shared
    // headless session installs no such lifetime, and injecting one makes the
    // session drive the lifetime's blocking main loop and deadlock. Global
    // open-window discovery is therefore verified by running the demo app (e2e),
    // not in this headless unit harness. Scoped queries are covered by the tests above.
    [Fact(Skip = "Requires a real IClassicDesktopStyleApplicationLifetime; verified via the demo app e2e instead.")]
    public void Query_With_Null_Scope_Searches_Open_TopLevels()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            var window = TestWindowFactory.Create(out var saveButton, out _);

            // Showing the window registers it as an open TopLevel, so a
            // null-scope query (all roots) must reach controls inside it.
            window.Show();
            try
            {
                var matches = registry.Query("Button[Name=Save]");
                Assert.Contains(saveButton, matches);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- PropertyValueSerializer: read + write round-trips ----------------

    [Fact]
    public void Serializer_Reads_Name_By_Clr_Property()
    {
        _session.RunOnUiThread(() =>
        {
            var serializer = new PropertyValueSerializer();
            _ = TestWindowFactory.Create(out var saveButton, out _);

            Assert.Equal(TestWindowFactory.ButtonName, serializer.Read(saveButton, "Name"));
        });
    }

    [Fact]
    public void Serializer_Reads_AvaloniaProperty_Value()
    {
        _session.RunOnUiThread(() =>
        {
            var serializer = new PropertyValueSerializer();
            _ = TestWindowFactory.Create(out var saveButton, out _);
            saveButton.Width = 64;

            var read = serializer.Read(saveButton, Layoutable.WidthProperty);
            Assert.Equal(64d, Assert.IsType<double>(read));
        });
    }

    [Fact]
    public void Serializer_TryWrite_Numeric_Width_RoundTrips()
    {
        _session.RunOnUiThread(() =>
        {
            var serializer = new PropertyValueSerializer();
            _ = TestWindowFactory.Create(out var saveButton, out _);

            var value = JsonDocument.Parse("123").RootElement;
            var ok = serializer.TryWrite(saveButton, "Width", value, out var error);

            Assert.True(ok, error);
            Assert.Equal(123d, saveButton.Width);

            // Read it back through the serializer to close the loop.
            Assert.Equal(123d, serializer.Read(saveButton, "Width"));
        });
    }

    [Fact]
    public void Serializer_TryWrite_Thickness_From_String_RoundTrips()
    {
        _session.RunOnUiThread(() =>
        {
            var serializer = new PropertyValueSerializer();
            _ = TestWindowFactory.Create(out var saveButton, out _);

            var value = JsonDocument.Parse("\"10,5,10,5\"").RootElement;
            var ok = serializer.TryWrite(saveButton, "Margin", value, out var error);

            Assert.True(ok, error);
            Assert.Equal(new Thickness(10, 5, 10, 5), saveButton.Margin);

            // The Read projection turns the Avalonia struct into its string form.
            Assert.Equal(new Thickness(10, 5, 10, 5).ToString(), serializer.Read(saveButton, "Margin"));
        });
    }

    [Fact]
    public void Serializer_TryWrite_Unknown_Property_Fails_Structured()
    {
        _session.RunOnUiThread(() =>
        {
            var serializer = new PropertyValueSerializer();
            _ = TestWindowFactory.Create(out var saveButton, out _);

            var value = JsonDocument.Parse("1").RootElement;
            var ok = serializer.TryWrite(saveButton, "NoSuchProperty", value, out var error);

            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(error));
        });
    }

    // ---- Visited-guard regression: cyclic merged logical+visual graph ------
    //
    // Hard-won v1 fix #1: every merged logical+visual tree walk carries a shared
    // HashSet<Visual> visited-guard. A real app's overlay/popup/adorner cross-links
    // make the SAME node reachable via both the logical and the visual tree, which
    // turns the merged graph cyclic even though neither single tree is. Without the
    // guard, SelectorChain.Descendants would recurse forever and StackOverflow the
    // host. This test reproduces that cyclic merge with a legal Avalonia construction
    // and asserts the public Query walk both terminates and still returns the target.

    [Fact]
    public void Query_Over_Cyclic_Merged_Logical_Visual_Graph_Terminates()
    {
        var matchCount = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            var window = CyclicGraphFactory.Create(out var target);

            // The cyclic merge would loop forever without the visited-guard. If the
            // guard is intact, this returns promptly with exactly the target control.
            var matches = registry.Query("Button[Name=Target]", scope: window);

            Assert.Contains(target, matches);
            return matches.Count;
        });

        // Sanity: the guarded walk terminated (we got here) and found just the target.
        Assert.Equal(1, matchCount);
    }

    [Fact]
    public void Cyclic_Graph_Would_Loop_Without_The_Visited_Guard()
    {
        // Guards against the test above becoming vacuous: prove the SAME graph is
        // genuinely cyclic by running an equivalent merged walk WITHOUT a cross-node
        // visited set. It must blow its recursion budget (i.e. it would loop forever),
        // confirming the visited-guard in SelectorChain.Descendants is load-bearing.
        _session.RunOnUiThread(() =>
        {
            var window = CyclicGraphFactory.Create(out _);

            Assert.Throws<InvalidOperationException>(
                () => GuardlessMergedWalk.Count(window, budget: 10_000));
        });
    }

    // ---- TODO: tool-level tests (enable once the Tools modules land) -------
    //
    // The Tools modules expose [McpServerTool] static methods (e.g. under
    // Keincheck.Tools) that take spine dependencies as DI parameters and run
    // their visual-tree work through UiDispatch.Run. Because UiDispatch.Run
    // executes synchronously when already on the UI thread, a tool method can be
    // invoked DIRECTLY inside HeadlessSession.RunOnUiThread and awaited.
    //
    // To add a tool test once a module lands, follow this template:
    //
    //   [Fact]
    //   public void FindControls_Returns_Handle_For_Save_Button()
    //   {
    //       var result = _session.RunOnUiThread(async () =>
    //       {
    //           var registry = new ControlRegistry();
    //           var window = TestWindowFactory.Create(out var saveButton, out _);
    //           window.Show();
    //           try
    //           {
    //               // Call the real tool method with its DI params supplied manually.
    //               return await InspectionTools.FindControls(registry, "Button[Name=Save]");
    //           }
    //           finally { window.Close(); }
    //       });
    //
    //       // Assert against the tool's JSON-serializable result shape, e.g.:
    //       //   var json = JsonSerializer.Serialize(result);
    //       //   using var doc = JsonDocument.Parse(json);
    //       //   Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    //   }
    //
    // Notes for the integrator wiring these up:
    //  * Use the Func<Task<T>> overload of RunOnUiThread for async tool methods.
    //  * Construct the spine dependencies (ControlRegistry, PropertyValueSerializer,
    //    McpServerOptions, BindingErrorSink) directly — the host's DI container is
    //    not running in unit tests; tool methods only need the instances passed in.
    //  * For tools that resolve by HANDLE, Assign the control first and pass the id.
    //  * For tools that take a TopLevel/selector scope, Show() the window so it is
    //    enumerated as an open root (or pass an explicit scope where supported).
}

/// <summary>
/// Builds a window whose merged logical+visual subtree is deliberately cyclic, to
/// exercise the visited-guard in <c>SelectorChain.Descendants</c>. The cycle lives
/// in the <em>merge</em> of the two trees — neither the logical nor the visual tree
/// is cyclic on its own, which mirrors a real overlay/adorner cross-link (the same
/// node reachable via both trees). A literal parent-cycle is impossible to build in
/// Avalonia (the framework recurses while wiring logical parents), so the relay
/// nodes are bare <see cref="FakeLogicalNode"/>s whose <c>LogicalChildren</c> we
/// populate directly, bypassing the parent-attachment machinery.
/// </summary>
internal static class CyclicGraphFactory
{
    /// <summary>
    /// Creates (does not show) a window: <c>Window → VisualHost → { Button "Target",
    /// relay a }</c>, where the relays form a cycle <c>a ⇆ b</c> and <c>a</c> also
    /// links back up to the host. The merged walk therefore loops host → a → b → a …
    /// unless the visited-guard breaks it. Must run on the UI thread.
    /// </summary>
    public static Window Create(out Button target)
    {
        target = new Button { Name = "Target", Content = "Target" };

        var host = new VisualHost { Name = "Host", Child = target };

        // Two relay nodes with a logical cycle a ⇆ b, plus a back-edge from a to the
        // host (an ancestor). Wiring goes through the relays' own lists, so it never
        // triggers Avalonia's logical-parent attachment (which would recurse).
        var a = new FakeLogicalNode();
        var b = new FakeLogicalNode();
        a.AddLogicalChild(b);
        b.AddLogicalChild(a);    // logical cycle a ⇆ b
        a.AddLogicalChild(host); // back-edge to an ancestor closes the merged cycle

        // Splice the relay graph into the VISUAL tree of the host so the selector
        // walk actually reaches it. A visual add does not run logical attachment.
        host.AddVisualChild(a);

        return new Window
        {
            Title = "Keincheck Cyclic Test Window",
            Width = 400,
            Height = 300,
            Content = host,
        };
    }
}

/// <summary>
/// A <see cref="Decorator"/> that exposes its protected <c>VisualChildren</c> so a
/// test can splice an extra visual child (a cyclic relay) into the visual subtree.
/// </summary>
internal sealed class VisualHost : Decorator
{
    public void AddVisualChild(Visual visual) => VisualChildren.Add(visual);
}

/// <summary>
/// A bare <see cref="Visual"/> that also implements <see cref="ILogical"/> (and
/// <see cref="ISetLogicalParent"/> as no-ops) with a fully caller-controlled
/// <c>LogicalChildren</c> list. Because the list is ours, adding a child does NOT
/// route through Avalonia's real logical-parent attachment, so we can wire a
/// back-edge to an ancestor and form a genuine cycle in the merged graph that the
/// selector walk (<c>Children</c> reads <c>ILogical.LogicalChildren</c>) consumes.
/// </summary>
internal sealed class FakeLogicalNode : Visual, ILogical, ISetLogicalParent
{
    private readonly Avalonia.Collections.AvaloniaList<ILogical> _logicalChildren = new();
    private ILogical? _logicalParent;

    public void AddLogicalChild(ILogical child) => _logicalChildren.Add(child);

    Avalonia.Collections.IAvaloniaReadOnlyList<ILogical> ILogical.LogicalChildren => _logicalChildren;
    ILogical? ILogical.LogicalParent => _logicalParent;
    bool ILogical.IsAttachedToLogicalTree => false;

    event EventHandler<LogicalTreeAttachmentEventArgs>? ILogical.AttachedToLogicalTree { add { } remove { } }
    event EventHandler<LogicalTreeAttachmentEventArgs>? ILogical.DetachedFromLogicalTree { add { } remove { } }

    void ILogical.NotifyAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e) { }
    void ILogical.NotifyDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e) { }
    void ILogical.NotifyResourcesChanged(ResourcesChangedEventArgs e) { }

    void ISetLogicalParent.SetParent(ILogical? parent) => _logicalParent = parent;
}

/// <summary>
/// An independent re-implementation of the merged logical+visual child walk
/// <em>without</em> a cross-node visited-guard, used solely to prove the test graph
/// is genuinely cyclic. Mirrors <c>SelectorChain.Children</c> (logical children
/// first, then visual, de-duplicated per node). Recurses with a budget so a cyclic
/// graph throws instead of overflowing the stack and crashing the test host.
/// </summary>
internal static class GuardlessMergedWalk
{
    public static int Count(Visual node, int budget)
    {
        if (budget <= 0)
            throw new InvalidOperationException("recursion budget exhausted — graph would loop forever");

        var count = 1;
        foreach (var child in Children(node))
            count += Count(child, budget - 1);
        return count;
    }

    private static IEnumerable<Visual> Children(Visual node)
    {
        var seen = new HashSet<Visual>(ReferenceEqualityComparer.Instance);

        if (node is ILogical logical)
        {
            foreach (var lc in logical.LogicalChildren)
            {
                if (lc is Visual v && seen.Add(v))
                    yield return v;
            }
        }

        foreach (var vc in node.GetVisualChildren())
        {
            if (seen.Add(vc))
                yield return vc;
        }
    }
}
