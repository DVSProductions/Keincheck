using Avalonia;
using Avalonia.Controls;

namespace AvaloniaMcp.Core;

/// <summary>
/// One simple selector: an optional type constraint plus zero or more
/// attribute predicates, or a bare <c>#Name</c> id selector.
/// </summary>
internal sealed class SimpleSelector
{
    private readonly string? _typeName;
    private readonly IReadOnlyList<(string Name, string Value)> _attributes;

    private SimpleSelector(string? typeName, IReadOnlyList<(string, string)> attributes)
    {
        _typeName = typeName;
        _attributes = attributes;
    }

    /// <summary>
    /// Parses a single simple-selector token, e.g. <c>Button</c>,
    /// <c>TextBox[Name=user]</c>, <c>#submit</c>, or <c>[Name='a b']</c>.
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
            return new SimpleSelector(null, new[] { ("Name", name) });
        }

        string? typeName = null;
        var attrs = new List<(string, string)>();

        var bracket = token.IndexOf('[');
        if (bracket < 0)
        {
            typeName = token;
        }
        else
        {
            if (bracket > 0)
                typeName = token[..bracket];

            var rest = token[bracket..];
            ParseAttributes(rest, attrs);
        }

        if (typeName is not null)
        {
            typeName = typeName.Trim();
            if (typeName.Length == 0)
                typeName = null;
            else if (!IsValidIdentifier(typeName))
                throw new FormatException($"invalid type name '{typeName}'");
        }

        if (typeName is null && attrs.Count == 0)
            throw new FormatException($"simple selector '{token}' matches nothing");

        return new SimpleSelector(typeName, attrs);
    }

    /// <summary>Tests whether <paramref name="visual"/> satisfies this simple selector.</summary>
    public bool Matches(Visual visual)
    {
        if (visual is not Control control)
            return false;

        if (_typeName is not null && !MatchesType(control, _typeName))
            return false;

        foreach (var (name, value) in _attributes)
        {
            if (!MatchesAttribute(control, name, value))
                return false;
        }

        return true;
    }

    private static bool MatchesType(Control control, string typeName)
    {
        for (var t = control.GetType(); t is not null && t != typeof(object); t = t.BaseType)
        {
            if (string.Equals(t.Name, typeName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool MatchesAttribute(Control control, string name, string value)
    {
        // The most common addressable attribute is Name; we also support a few
        // other commonly-queried string-ish properties generically via reflection.
        if (string.Equals(name, "Name", StringComparison.Ordinal))
            return string.Equals(control.Name, value, StringComparison.Ordinal);

        var prop = control.GetType().GetProperty(
            name,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (prop is null || prop.GetIndexParameters().Length > 0)
            return false;

        object? actual;
        try
        {
            actual = prop.GetValue(control);
        }
        catch
        {
            return false;
        }

        return string.Equals(actual?.ToString(), value, StringComparison.Ordinal);
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
