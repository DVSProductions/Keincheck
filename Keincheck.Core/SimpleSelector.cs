namespace Keincheck.Core;

/// <summary>
/// One simple selector: an optional type constraint, zero or more style-class predicates
/// (<c>.class</c>), and zero or more attribute predicates, or a bare <c>#Name</c> id
/// selector. Matching is driven through <see cref="IUiAdapter"/> so the selector engine
/// stays framework-free.
/// </summary>
internal sealed class SimpleSelector
{
    private readonly string? _typeName;
    private readonly IReadOnlyList<string> _classes;
    private readonly IReadOnlyList<(string Name, string Value)> _attributes;

    private SimpleSelector(string? typeName, IReadOnlyList<string> classes, IReadOnlyList<(string, string)> attributes)
    {
        _typeName = typeName;
        _classes = classes;
        _attributes = attributes;
    }

    /// <summary>
    /// Parses a single simple-selector token, e.g. <c>Button</c>,
    /// <c>TextBox[Name=user]</c>, <c>#submit</c>, <c>.toolGroup</c>, <c>Button.primary</c>,
    /// <c>.a.b</c>, <c>.primary[Name=Save]</c>, or <c>[Name='a b']</c>.
    /// </summary>
    public static SimpleSelector Parse(string token)
    {
        token = token.Trim();
        if (token.Length == 0)
            throw new FormatException("empty simple selector");

        // #Name id selector: sugar for [Name=Name] with no type constraint.
        if (token[0] == '#')
        {
            var name = token[1..];
            if (name.Length == 0)
                throw new FormatException("'#' must be followed by a name");
            return new SimpleSelector(null, Array.Empty<string>(), new[] { ("Name", name) });
        }

        string? typeName = null;
        var classes = new List<string>();
        var attrs = new List<(string, string)>();

        // Split off the attribute tail ([...]) first; the head holds the optional type name
        // and any .class segments (e.g. "Button.primary.big" or ".toolGroup").
        var bracket = token.IndexOf('[');
        var head = bracket < 0 ? token : token[..bracket];
        if (bracket >= 0)
            ParseAttributes(token[bracket..], attrs);

        ParseHead(head, ref typeName, classes);

        if (typeName is null && classes.Count == 0 && attrs.Count == 0)
            throw new FormatException($"simple selector '{token}' matches nothing");

        return new SimpleSelector(typeName, classes, attrs);
    }

    /// <summary>
    /// Parses the pre-bracket head of a simple selector into an optional leading type name
    /// followed by zero or more <c>.class</c> segments. A leading <c>.</c> means no type
    /// constraint. Each class name is validated as an identifier (not treated as a type).
    /// </summary>
    private static void ParseHead(string head, ref string? typeName, List<string> classes)
    {
        head = head.Trim();
        if (head.Length == 0)
            return;

        var dot = head.IndexOf('.');

        // Leading type name (the run before the first '.'), if any.
        if (dot != 0)
        {
            var name = (dot < 0 ? head : head[..dot]).Trim();
            if (name.Length != 0)
            {
                if (!IsValidIdentifier(name))
                    throw new FormatException($"invalid type name '{name}'");
                typeName = name;
            }
        }

        if (dot < 0)
            return;

        // Remaining '.'-separated class segments. A bare/empty segment (e.g. "." or "..")
        // is malformed and throws (ControlRegistry.Query catches it to an empty result).
        foreach (var seg in head[(dot + 1)..].Split('.'))
        {
            var cls = seg.Trim();
            if (cls.Length == 0)
                throw new FormatException($"empty class name in selector '{head}'");
            if (!IsValidIdentifier(cls))
                throw new FormatException($"invalid class name '{cls}'");
            classes.Add(cls);
        }
    }

    /// <summary>Tests whether <paramref name="element"/> satisfies this simple selector.</summary>
    public bool Matches(object element, IUiAdapter ui)
    {
        if (!ui.IsControl(element))
            return false;

        if (_typeName is not null && !ui.MatchesType(element, _typeName))
            return false;

        if (_classes.Count > 0)
        {
            // The element must carry EVERY requested class (ordinal, case-sensitive).
            var present = ui.GetClasses(element);
            var set = present as ICollection<string> ?? present.ToArray();
            foreach (var cls in _classes)
            {
                if (!set.Contains(cls))
                    return false;
            }
        }

        foreach (var (name, value) in _attributes)
        {
            if (!MatchesAttribute(element, name, value, ui))
                return false;
        }

        return true;
    }

    private static bool MatchesAttribute(object element, string name, string value, IUiAdapter ui)
    {
        // The most common addressable attribute is Name; we also support a few other
        // commonly-queried string-ish properties generically. Reads route through the
        // adapter (which projects framework values to JSON-friendly forms); the
        // comparison is ordinal against the string projection, matching v1 behavior.
        if (string.Equals(name, "Name", StringComparison.Ordinal))
            return string.Equals(ui.GetName(element), value, StringComparison.Ordinal);

        if (!ui.TryReadProperty(element, name, out var actual) || actual is null)
            return false;

        return string.Equals(actual.ToString(), value, StringComparison.Ordinal);
    }

    private static void ParseAttributes(string rest, List<(string, string)> attrs)
    {
        var i = 0;
        while (i < rest.Length)
        {
            if (rest[i] != '[')
                throw new FormatException($"expected '[' at position {i} in '{rest}'");

            var close = rest.IndexOf(']', i + 1);
            if (close < 0)
                throw new FormatException($"unterminated attribute selector in '{rest}'");

            var inner = rest[(i + 1)..close].Trim();
            var eq = inner.IndexOf('=');
            if (eq <= 0)
                throw new FormatException($"attribute selector must be [Name=value], got '[{inner}]'");

            var name = inner[..eq].Trim();
            var value = inner[(eq + 1)..].Trim();
            value = Unquote(value);

            if (name.Length == 0)
                throw new FormatException("attribute name cannot be empty");

            attrs.Add((name, value));
            i = close + 1;
        }
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 &&
            ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"')))
        {
            return s[1..^1];
        }
        return s;
    }

    private static bool IsValidIdentifier(string s)
    {
        if (s.Length == 0)
            return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_'))
            return false;
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }
}
