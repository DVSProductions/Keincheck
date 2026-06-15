using System.Collections.Concurrent;

namespace Keincheck.Core;

/// <summary>
/// Central directory that hands out stable string handles for UI elements and
/// resolves elements from handles or a CSS-ish selector. A singleton in the MCP
/// host's DI container. All weak references — assigning a handle never keeps an
/// element alive.
/// </summary>
/// <remarks>
/// <para>
/// Elements are opaque <see cref="object"/> handles supplied by the active
/// <see cref="IUiAdapter"/>; the registry never inspects their concrete framework
/// type. Selector resolution (<see cref="Query"/>) is driven <i>through</i> the
/// adapter — root enumeration, child walks, and metadata all come from
/// <see cref="IUiAdapter"/>, so the registry stays framework-free.
/// </para>
/// Selector grammar (whitespace-separated combinator chain):
/// <list type="bullet">
///   <item><c>Type</c> — matches by control type name (e.g. <c>Button</c>). Matches exact runtime type name or any base type name.</item>
///   <item><c>.class</c> — matches by author style-class membership (e.g. <c>.toolGroup</c>); ordinal / case-sensitive. Combinable: <c>Type.class</c>, multi-class <c>.a.b</c> (requires all), and <c>.class[Name=x]</c>. Matches nothing on frameworks without style classes (e.g. WPF).</item>
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
    private readonly ConcurrentDictionary<string, WeakReference<object>> _byId = new();
    private readonly ConditionalWeakTableShim _byElement = new();
    private long _counter;

    /// <summary>
    /// Assigns (or returns the existing) stable handle for <paramref name="element"/>.
    /// Idempotent: the same element always maps to the same id for its lifetime.
    /// </summary>
    public string Assign(object element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (_byElement.TryGet(element, out var existing) &&
            _byId.TryGetValue(existing, out var wr) &&
            wr.TryGetTarget(out var live) &&
            ReferenceEquals(live, element))
        {
            return existing;
        }

        var id = "ctl-" + Interlocked.Increment(ref _counter).ToString("x");
        _byId[id] = new WeakReference<object>(element);
        _byElement.Set(element, id);
        return id;
    }

    /// <summary>
    /// Resolves a previously assigned handle. Returns <c>false</c> (and a null out) if
    /// the id is unknown or the element has been collected/closed. Prunes dead entries
    /// on miss.
    /// </summary>
    public bool TryResolve(string id, out object? element)
    {
        element = null;
        if (string.IsNullOrEmpty(id))
            return false;

        if (_byId.TryGetValue(id, out var wr))
        {
            if (wr.TryGetTarget(out var live))
            {
                element = live;
                return true;
            }

            // Dead reference — clean it up.
            _byId.TryRemove(id, out _);
        }

        return false;
    }

    /// <summary>
    /// Evaluates a CSS-ish <paramref name="selector"/> over the logical+visual tree,
    /// driven through <paramref name="ui"/>. When <paramref name="scope"/> is null,
    /// every open top-level in the current application is searched. Results are
    /// de-duplicated and returned in document order per root. Returns an empty list for
    /// a null or blank selector (never throws on a malformed selector — returns empty).
    /// </summary>
    public IReadOnlyList<object> Query(string selector, IUiAdapter ui, object? scope = null)
    {
        ArgumentNullException.ThrowIfNull(ui);

        if (string.IsNullOrWhiteSpace(selector))
            return Array.Empty<object>();

        SelectorChain chain;
        try
        {
            chain = SelectorChain.Parse(selector);
        }
        catch (FormatException)
        {
            return Array.Empty<object>();
        }

        var roots = scope is not null
            ? new[] { scope }
            : ui.EnumerateRoots().ToArray();

        var results = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var root in roots)
        {
            foreach (var match in chain.Match(root, ui))
            {
                if (seen.Add(match))
                    results.Add(match);
            }
        }

        return results;
    }

    /// <summary>
    /// Lightweight weak element-&gt;id map. We avoid a real
    /// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>
    /// generic-over-string boxing concern by storing the id string directly.
    /// </summary>
    private sealed class ConditionalWeakTableShim
    {
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> _table = new();

        public bool TryGet(object e, out string id) => _table.TryGetValue(e, out id!);

        public void Set(object e, string id)
        {
            _table.Remove(e);
            _table.Add(e, id);
        }
    }
}
