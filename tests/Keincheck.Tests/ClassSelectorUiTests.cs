using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Keincheck.Avalonia;
using Keincheck.Core;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Tests for finding-4: the <c>.class</c> selector, advertised in the Hub guide but
/// previously unimplemented. The fix added a defaulted <see cref="IUiAdapter.GetClasses"/>
/// seam member (empty by default, overridden by <see cref="AvaloniaUiAdapter"/> over
/// <c>StyledElement.Classes</c>) and a <c>.class</c> parse path in
/// <see cref="SimpleSelector"/>. These exercise matching against a real Avalonia control
/// that carries author style classes, driven through the neutral
/// <see cref="ControlRegistry"/>/<see cref="IUiAdapter"/> surface.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class ClassSelectorUiTests
{
    private readonly HeadlessSession _session;

    public ClassSelectorUiTests(HeadlessSession session) => _session = session;

    private static IUiAdapter NewAdapter() => new AvaloniaUiAdapter(new PropertyValueSerializer(8));

    /// <summary>
    /// A window whose single Button carries the author classes {toolGroup, primary}. The
    /// classes are set on the typed control here (framework construction), but every
    /// assertion downstream goes through the neutral seam.
    /// </summary>
    private static Window ClassWindow(out Button classed)
    {
        classed = new Button { Name = "Tagged", Content = "Tag" };
        classed.Classes.Add("toolGroup");
        classed.Classes.Add("primary");

        var plain = new Button { Name = "Plain", Content = "Plain" };

        var panel = new StackPanel();
        panel.Children.Add(classed);
        panel.Children.Add(plain);
        return new Window { Width = 200, Height = 120, Content = panel };
    }

    // ---- GetClasses seam ---------------------------------------------------

    [Fact]
    public void GetClasses_Reports_Author_Classes_For_Avalonia_Control()
    {
        _session.RunOnUiThread(() =>
        {
            IUiAdapter ui = NewAdapter();
            _ = ClassWindow(out var classed);

            var classes = ui.GetClasses(classed).ToHashSet();
            Assert.Contains("toolGroup", classes);
            Assert.Contains("primary", classes);
        });
    }

    [Fact]
    public void GetClasses_Default_Is_Empty_For_NonStyled_Handle()
    {
        // A bare object is not a StyledElement, so the Avalonia override returns empty —
        // mirroring the IUiAdapter default that WPF/headless adapters inherit, where
        // .class matches nothing rather than throwing.
        IUiAdapter ui = NewAdapter();
        Assert.Empty(ui.GetClasses(new object()));
    }

    // ---- .class matching via the registry ---------------------------------

    [Fact]
    public void Query_By_Class_Matches_Only_The_Classed_Control()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            object window = ClassWindow(out var classed);

            IReadOnlyList<object> matches = registry.Query(".toolGroup", ui, window);

            Assert.Same(classed, Assert.Single(matches));
        });
    }

    [Fact]
    public void Query_By_Type_And_Class_Matches()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            object window = ClassWindow(out var classed);

            IReadOnlyList<object> matches = registry.Query("Button.toolGroup", ui, window);

            Assert.Same(classed, Assert.Single(matches));
        });
    }

    [Fact]
    public void Query_By_Unknown_Class_Matches_Nothing()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            object window = ClassWindow(out _);

            Assert.Empty(registry.Query(".other", ui, window));
        });
    }

    [Fact]
    public void Query_By_Multiple_Classes_Requires_All()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            object window = ClassWindow(out var classed);

            // Both classes present -> matches the classed control.
            Assert.Same(classed, Assert.Single(registry.Query(".toolGroup.primary", ui, window)));

            // One present, one absent -> no match (every requested class must be present).
            Assert.Empty(registry.Query(".toolGroup.missing", ui, window));
        });
    }

    [Fact]
    public void Query_By_Class_And_Attribute_Composes()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            object window = ClassWindow(out var classed);

            // .class + [Name=…] together: both predicates must hold.
            Assert.Same(classed, Assert.Single(registry.Query(".toolGroup[Name=Tagged]", ui, window)));
            // Same class but a non-matching Name -> no match.
            Assert.Empty(registry.Query(".toolGroup[Name=Plain]", ui, window));
        });
    }

    // ---- malformed selectors never throw out of Query ---------------------

    [Fact]
    public void Bare_Dot_Selector_Returns_Empty_Never_Throws()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            object window = ClassWindow(out _);

            // A bare "." (empty class name) is malformed; ControlRegistry.Query must catch
            // the FormatException and return an empty result rather than throw.
            Assert.Empty(registry.Query(".", ui, window));
            Assert.Empty(registry.Query("..", ui, window));
            Assert.Empty(registry.Query("Button.", ui, window));
        });
    }
}
