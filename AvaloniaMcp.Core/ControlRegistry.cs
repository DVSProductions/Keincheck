using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace AvaloniaMcp.Core;

/// <summary>
/// Central directory that hands out stable string handles for controls and
/// resolves controls from handles or a CSS-ish selector. A singleton in the
/// MCP host's DI container. All weak references — assigning a handle never
/// keeps a control alive.
/// </summary>
/// <remarks>
/// Selector grammar (whitespace-separated combinator chain):
/// <list type="bullet">
///   <item><c>Type</c> — matches by control type name (e.g. <c>Button</c>). Matches exact runtime type name or any base type name.</item>
///   <item><c>Type[Name=x]</c> — type plus a <c>Name</c> attribute equal to <c>x</c>.</item>
///   <item><c>#Name</c> — matches any control whose <c>Name</c> equals <c>Name</c>.</item>
///   <item><c>A B</c> — descendant combinator: <c>B</c> anywhere under <c>A</c>.</item>
///   <item><c>A &gt; B</c> — child combinator: <c>B</c> that is a direct child of <c>A</c>.</item>
/// </list>
/// Attribute predicates also accept <c>[Name=x]</c> standalone and quoting:
/// <c>[Name='my value']</c> or <c>[Name="my value"]</c>.
/// </remarks>
public sealed class ControlRegistry
{
    private readonly ConcurrentDictionary<string, WeakReference<Control>> _byId = new();
    private readonly ConditionalWeakTableShim _byControl = new();
    private long _counter;

    /// <summary>
    /// Assigns (or returns the existing) stable handle for <paramref name="control"/>.
    /// Idempotent: the same control always maps to the same id for its lifetime.
    /// </summary>
    public string Assign(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (_byControl.TryGet(control, out var existing) &&
            _byId.TryGetValue(existing, out var wr) &&
            wr.TryGetTarget(out var live) &&
            ReferenceEquals(live, control))
        {
            return existing;
        }

        var id = "ctl-" + Interlocked.Increment(ref _counter).ToString("x");
        _byId[id] = new WeakReference<Control>(control);
        _byControl.Set(control, id);
        return id;
    }

    /// <summary>
    /// Resolves a previously assigned handle. Returns <c>false</c> (and a null
    /// out) if the id is unknown or the control has been collected/closed.
    /// Prunes dead entries on miss.
    /// </summary>
    public bool TryResolve(string id, out Control? control)
    {
        control = null;
        if (string.IsNullOrEmpty(id))
            return false;

        if (_byId.TryGetValue(id, out var wr))
        {
            if (wr.TryGetTarget(out var live))
            {
                control = live;
                return true;
            }

            // Dead reference — clean it up.
            _byId.TryRemove(id, out _);
        }

        return false;
    }

    /// <summary>
    /// Evaluates a CSS-ish <paramref name="selector"/> over the logical+visual
    /// tree. When <paramref name="scope"/> is null, every open <c>TopLevel</c>
    /// in the current application is searched. Results are de-duplicated and
    /// returned in document order per root. Returns an empty list for a null or
    /// blank selector (never throws on a malformed selector — returns empty).
    /// </summary>
    public IReadOnlyList<Control> Query(string selector, TopLevel? scope = null)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return Array.Empty<Control>();

        SelectorChain chain;
        try
        {
            chain = SelectorChain.Parse(selector);
        }
        catch (FormatException)
        {
            return Array.Empty<Control>();
        }

        var roots = scope is not null
            ? new[] { (Visual)scope }
            : EnumerateRoots().ToArray();

        var results = new List<Control>();
        var seen = new HashSet<Control>(ReferenceEqualityComparer.Instance);

        foreach (var root in roots)
        {
            foreach (var match in chain.Match(root))
            {
                if (seen.Add(match))
                    results.Add(match);
            }
        }

        return results;
    }

    /// <summary>
    /// All open top-level visuals (windows, popups) of the current application.
    /// Safe to call only on the UI thread.
    /// </summary>
    public static IEnumerable<Visual> EnumerateRoots()
    {
        var app = Application.Current;
        if (app is null)
            yield break;

        switch (app.ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                foreach (var w in desktop.Windows)
                    yield return w;
                break;
            case ISingleViewApplicationLifetime single when single.MainView is { } mv:
                if (TopLevel.GetTopLevel(mv) is { } tl)
                    yield return tl;
                else
                    yield return mv;
                break;
        }
    }

    /// <summary>
    /// Lightweight weak control-&gt;id map. We avoid a real
    /// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>
    /// generic-over-string boxing concern by storing the id string directly.
    /// </summary>
    private sealed class ConditionalWeakTableShim
    {
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, string> _table = new();

        public bool TryGet(Control c, out string id) => _table.TryGetValue(c, out id!);

        public void Set(Control c, string id)
        {
            _table.Remove(c);
            _table.Add(c, id);
        }
    }
}
