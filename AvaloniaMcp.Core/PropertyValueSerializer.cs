using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace AvaloniaMcp.Core;

/// <summary>
/// Converts control property values to/from JSON-friendly representations.
/// Reading produces a value safe to embed in an MCP JSON result; writing
/// coerces a <see cref="JsonElement"/> back onto a property using
/// <see cref="TypeDescriptor"/> converters (so e.g. <c>"10,5,10,5"</c> becomes
/// a <see cref="Thickness"/>).
/// </summary>
public sealed class PropertyValueSerializer
{
    private readonly int _maxDepth;

    /// <param name="maxDepth">
    /// Maximum object graph depth when reading complex values. Defaults to a
    /// conservative 8; the host passes <see cref="McpServerOptions.MaxSerializationDepth"/>.
    /// </param>
    public PropertyValueSerializer(int maxDepth = 8)
    {
        _maxDepth = Math.Max(1, maxDepth);
    }

    /// <summary>
    /// Reads the value of an <see cref="AvaloniaProperty"/> on a control and
    /// returns a JSON-serializable representation (primitive, string, or a
    /// small dictionary/array for complex values).
    /// </summary>
    public object? Read(Control control, AvaloniaProperty property)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(property);
        return ToJsonFriendly(control.GetValue(property), _maxDepth);
    }

    /// <summary>
    /// Reads a CLR property by name and returns a JSON-serializable value.
    /// Returns <c>null</c> if the property does not exist or is not readable.
    /// </summary>
    public object? Read(Control control, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentException.ThrowIfNullOrEmpty(propertyName);

        var prop = FindClrProperty(control.GetType(), propertyName);
        if (prop is null || !prop.CanRead || prop.GetIndexParameters().Length > 0)
            return null;

        try
        {
            return ToJsonFriendly(prop.GetValue(control), _maxDepth);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Coerces <paramref name="value"/> onto the named CLR property of
    /// <paramref name="control"/>. Returns <c>true</c> on success; on failure
    /// returns <c>false</c> with a human-readable reason in
    /// <paramref name="error"/>. Never throws for ordinary failures.
    /// </summary>
    public bool TryWrite(Control control, string propertyName, JsonElement value, out string error)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (string.IsNullOrEmpty(propertyName))
        {
            error = "Property name is required.";
            return false;
        }

        var prop = FindClrProperty(control.GetType(), propertyName);
        if (prop is null)
        {
            error = $"Property '{propertyName}' not found on {control.GetType().Name}.";
            return false;
        }

        if (!prop.CanWrite || prop.SetMethod is null || !prop.SetMethod.IsPublic)
        {
            error = $"Property '{propertyName}' is not writable.";
            return false;
        }

        if (prop.GetIndexParameters().Length > 0)
        {
            error = $"Property '{propertyName}' is an indexer and cannot be set.";
            return false;
        }

        if (!TryCoerce(value, prop.PropertyType, out var coerced, out error))
            return false;

        try
        {
            prop.SetValue(control, coerced);
            error = string.Empty;
            return true;
        }
        catch (TargetInvocationException tie)
        {
            error = $"Setting '{propertyName}' threw: {tie.InnerException?.Message ?? tie.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Setting '{propertyName}' failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Attempts to coerce a <see cref="JsonElement"/> into <paramref name="targetType"/>.
    /// Honors nullable targets, enums, primitives, and any type with a
    /// <see cref="TypeConverter"/> capable of parsing a string (e.g. Thickness,
    /// Brush, Color, GridLength).
    /// </summary>
    public static bool TryCoerce(JsonElement value, Type targetType, out object? result, out string error)
    {
        result = null;
        error = string.Empty;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null)
                return true; // result stays null
            error = $"Cannot assign null to non-nullable {targetType.Name}.";
            return false;
        }

        try
        {
            // Direct primitive paths.
            if (underlying == typeof(string))
            {
                result = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                return true;
            }

            if (underlying == typeof(bool))
            {
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    result = value.GetBoolean();
                    return true;
                }
            }
            else if (underlying.IsEnum)
            {
                var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                result = Enum.Parse(underlying, raw, ignoreCase: true);
                return true;
            }
            else if (IsNumeric(underlying))
            {
                var raw = value.ValueKind == JsonValueKind.Number
                    ? value.GetRawText()
                    : value.GetString();
                if (raw is not null)
                {
                    result = Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
                    return true;
                }
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

            // TypeConverter path (some types register a System.ComponentModel converter).
            var converter = TypeDescriptor.GetConverter(underlying);
            if (text is not null && converter.CanConvertFrom(typeof(string)))
            {
                try
                {
                    result = converter.ConvertFromInvariantString(text);
                    return true;
                }
                catch
                {
                    // fall through to the Parse path
                }
            }

            // Avalonia value types (Thickness, Color, GridLength, Point, CornerRadius,
            // Size, etc.) expose a static Parse(string) — and sometimes
            // Parse(string, IFormatProvider) — rather than a ComponentModel converter.
            if (text is not null && TryStaticParse(underlying, text, out result))
                return true;

            // Last resort: assignable-from-string.
            if (underlying.IsAssignableFrom(typeof(string)) && text is not null)
            {
                result = text;
                return true;
            }

            error = $"No conversion from JSON {value.ValueKind} to {underlying.Name}.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Conversion to {underlying.Name} failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Invokes a public static <c>Parse(string)</c> or
    /// <c>Parse(string, IFormatProvider)</c> on <paramref name="targetType"/> if
    /// one exists. Covers Avalonia structs (Thickness, Color, GridLength, …) that
    /// parse from their invariant string form.
    /// </summary>
    private static bool TryStaticParse(Type targetType, string text, out object? result)
    {
        result = null;

        var withProvider = targetType.GetMethod(
            "Parse",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string), typeof(IFormatProvider) },
            modifiers: null);
        if (withProvider is not null && withProvider.ReturnType == targetType)
        {
            try
            {
                result = withProvider.Invoke(null, new object?[] { text, CultureInfo.InvariantCulture });
                return true;
            }
            catch
            {
                return false;
            }
        }

        var simple = targetType.GetMethod(
            "Parse",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string) },
            modifiers: null);
        if (simple is not null && simple.ReturnType == targetType)
        {
            try
            {
                result = simple.Invoke(null, new object?[] { text });
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static PropertyInfo? FindClrProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    private static bool IsNumeric(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) ||
        t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) ||
        t == typeof(long) || t == typeof(ulong) ||
        t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    /// <summary>
    /// Reduces an arbitrary value to something <see cref="JsonSerializer"/> can
    /// emit without reflection surprises or cycles.
    /// </summary>
    private object? ToJsonFriendly(object? value, int depth)
    {
        if (value is null)
            return null;

        switch (value)
        {
            case string:
            case bool:
            case byte or sbyte or short or ushort or int or uint or long or ulong:
            case float or double or decimal:
                return value;
        }

        var type = value.GetType();

        if (type.IsEnum)
            return value.ToString();

        if (value is Control control)
            return $"{control.GetType().Name}{(string.IsNullOrEmpty(control.Name) ? "" : "#" + control.Name)}";

        if (depth <= 0)
            return value.ToString();

        // Known Avalonia structs serialize cleanly via their invariant string form.
        if (type.Namespace?.StartsWith("Avalonia", StringComparison.Ordinal) == true && type.IsValueType)
            return value.ToString();

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var items = new List<object?>();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= 50) // cap collection size in output
                {
                    items.Add("…(truncated)");
                    break;
                }
                items.Add(ToJsonFriendly(item, depth - 1));
            }
            return items;
        }

        // Fall back to the type's string representation; ToString() is the
        // safest JSON-friendly projection for opaque reference types.
        return value.ToString();
    }
}
