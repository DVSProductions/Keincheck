using System.Text;

namespace Keincheck.Core;

/// <summary>
/// A parsed CSS-ish selector: a sequence of simple selectors joined by descendant
/// (whitespace) or child (<c>&gt;</c>) combinators. Matching walks the merged
/// logical+visual tree under a given root, driven through <see cref="IUiAdapter"/>.
/// </summary>
internal sealed class SelectorChain
{
    private readonly IReadOnlyList<Step> _steps;

    private SelectorChain(IReadOnlyList<Step> steps) => _steps = steps;

    private enum Combinator
    {
        /// <summary>First step in the chain; matched anywhere under the root.</summary>
        Root,
        /// <summary>Descendant combinator (whitespace).</summary>
        Descendant,
        /// <summary>Child combinator (<c>&gt;</c>).</summary>
        Child,
    }

    private readonly record struct Step(Combinator Combinator, SimpleSelector Selector);

    /// <summary>
    /// Parses a selector string. Throws <see cref="FormatException"/> on a
    /// structurally invalid selector (caller catches and returns empty).
    /// </summary>
    public static SelectorChain Parse(string selector)
    {
        var tokens = Tokenize(selector);
        if (tokens.Count == 0)
            throw new FormatException("empty selector");

        var steps = new List<Step>();
        var combinator = Combinator.Root;
        var i = 0;

        while (i < tokens.Count)
        {
            var tok = tokens[i];
            if (tok == ">")
            {
                if (steps.Count == 0)
                    throw new FormatException("selector cannot start with '>'");
                combinator = Combinator.Child;
                i++;
                if (i >= tokens.Count)
                    throw new FormatException("selector cannot end with '>'");
                continue;
            }

            var simple = SimpleSelector.Parse(tok);
            steps.Add(new Step(combinator, simple));
            combinator = Combinator.Descendant; // default between simple selectors
            i++;
        }

        return new SelectorChain(steps);
    }

    /// <summary>
    /// Enumerates every control under (and including) <paramref name="root"/> that
    /// matches the full chain, walking the tree through <paramref name="ui"/>.
    /// </summary>
    public IEnumerable<object> Match(object root, IUiAdapter ui)
    {
        // Candidate set for the first step: root + all descendants matching step[0].
        IEnumerable<object> current = Descendants(root, ui, includeSelf: true)
            .Where(v => _steps[0].Selector.Matches(v, ui));

        for (var s = 1; s < _steps.Count; s++)
        {
            var step = _steps[s];
            current = step.Combinator switch
            {
                Combinator.Child => current.SelectMany(parent =>
                    Children(parent, ui).Where(c => step.Selector.Matches(c, ui))),
                _ => current.SelectMany(ancestor =>
                    Descendants(ancestor, ui, includeSelf: false).Where(c => step.Selector.Matches(c, ui))),
            };
        }

        // Distinct, only controls.
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var v in current)
        {
            if (ui.IsControl(v) && seen.Add(v))
                yield return v;
        }
    }

    /// <summary>
    /// Depth-first traversal of the merged logical+visual subtree. Logical children
    /// are preferred (they are the author-facing tree); visual-only children (template
    /// parts) are also included so template content is reachable.
    /// </summary>
    private static IEnumerable<object> Descendants(object root, IUiAdapter ui, bool includeSelf)
    {
        // Iterative pre-order DFS with a visited guard. The merged logical+visual graph
        // can contain cycles (e.g. popup/overlay/adorner cross-links between the two
        // trees), so a naive recursive walk overflows the stack. The visited set breaks
        // cycles and de-duplicates diamond paths; pushing children in reverse preserves
        // document order on pop.
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        stack.Push(root);
        var isRoot = true;

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!visited.Add(node))
                continue;

            if (!(isRoot && !includeSelf))
                yield return node;
            isRoot = false;

            var children = Children(node, ui).ToList();
            for (var k = children.Count - 1; k >= 0; k--)
            {
                if (!visited.Contains(children[k]))
                    stack.Push(children[k]);
            }
        }
    }

    /// <summary>
    /// Direct children of a node, merging logical and visual children without
    /// duplicates. This lets selectors reach both author-declared content and
    /// template-generated parts.
    /// </summary>
    private static IEnumerable<object> Children(object node, IUiAdapter ui)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var lc in ui.GetLogicalChildren(node))
        {
            if (seen.Add(lc))
                yield return lc;
        }

        foreach (var vc in ui.GetVisualChildren(node))
        {
            if (seen.Add(vc))
                yield return vc;
        }
    }

    /// <summary>
    /// Splits a selector into combinator tokens. Whitespace separates simple
    /// selectors; a bare <c>&gt;</c> becomes its own token. Brackets and quotes are
    /// kept intact so attribute values may contain spaces. A <c>.</c> is NOT a
    /// combinator — it falls through the default branch and stays attached to its
    /// token (so <c>Button.primary</c> / <c>.a.b</c> survive as one token for
    /// <see cref="SimpleSelector.Parse"/> to split into type + class predicates).
    /// </summary>
    private static List<string> Tokenize(string selector)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var depth = 0;          // inside [ ]
        char quote = '\0';      // active quote char inside brackets

        void Flush()
        {
            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }

        foreach (var ch in selector)
        {
            if (quote != '\0')
            {
                sb.Append(ch);
                if (ch == quote)
                    quote = '\0';
                continue;
            }

            switch (ch)
            {
                case '\'':
                case '"':
                    quote = ch;
                    sb.Append(ch);
                    break;
                case '[':
                    depth++;
                    sb.Append(ch);
                    break;
                case ']':
                    depth = Math.Max(0, depth - 1);
                    sb.Append(ch);
                    break;
                case '>' when depth == 0:
                    Flush();
                    tokens.Add(">");
                    break;
                default:
                    if (char.IsWhiteSpace(ch) && depth == 0)
                        Flush();
                    else
                        sb.Append(ch);
                    break;
            }
        }

        Flush();
        return tokens;
    }
}
