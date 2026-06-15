using System.Globalization;
using Avalonia.Logging;

namespace Keincheck.Core;

/// <summary>
/// An <see cref="ILogSink"/> that captures Avalonia binding (and optionally
/// other) log messages into a bounded in-memory ring buffer, then forwards to
/// any previously-installed sink so normal logging keeps working.
/// </summary>
/// <remarks>
/// Install once with <see cref="Install"/>, which swaps it in as
/// <c>Logger.Sink</c> and chains the prior sink. Thread-safe.
/// </remarks>
public sealed class BindingErrorSink : ILogSink
{
    private readonly object _gate = new();
    private readonly string[] _ring;
    private readonly ILogSink? _inner;
    private readonly bool _bindingOnly;
    private int _head;
    private int _count;

    /// <param name="capacity">Ring buffer size (number of retained messages).</param>
    /// <param name="inner">Existing sink to forward to (may be null).</param>
    /// <param name="bindingOnly">
    /// When true (default) only <see cref="LogArea.Binding"/> messages are
    /// buffered; all messages are still forwarded to <paramref name="inner"/>.
    /// </param>
    public BindingErrorSink(int capacity = 256, ILogSink? inner = null, bool bindingOnly = true)
    {
        _ring = new string[Math.Max(1, capacity)];
        _inner = inner;
        _bindingOnly = bindingOnly;
    }

    /// <summary>The currently-installed sink, if installation happened via <see cref="Install"/>.</summary>
    public static BindingErrorSink? Current { get; private set; }

    /// <summary>
    /// Installs a sink as <c>Logger.Sink</c>, chaining whatever was there
    /// before. Returns the installed sink. Idempotent-ish: calling again wraps
    /// the current sink again, so call once at host startup.
    /// </summary>
    public static BindingErrorSink Install(int capacity = 256, bool bindingOnly = true)
    {
        var previous = Logger.Sink;
        var sink = new BindingErrorSink(capacity, previous, bindingOnly);
        Logger.Sink = sink;
        Current = sink;
        return sink;
    }

    /// <summary>
    /// Restores the inner sink that was active before this one, undoing
    /// <see cref="Install"/>. Safe to call even if this sink is not current.
    /// </summary>
    public void Uninstall()
    {
        if (ReferenceEquals(Logger.Sink, this))
        {
            Logger.Sink = _inner;
            if (ReferenceEquals(Current, this))
                Current = _inner as BindingErrorSink;
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(LogEventLevel level, string area)
    {
        // We want binding events at Warning+; defer to inner for the rest so we
        // don't suppress anyone else's logging.
        if (!_bindingOnly || string.Equals(area, LogArea.Binding, StringComparison.Ordinal))
            return true;
        return _inner?.IsEnabled(level, area) ?? false;
    }

    /// <inheritdoc />
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        Capture(level, area, messageTemplate, null);
        _inner?.Log(level, area, source, messageTemplate);
    }

    /// <inheritdoc />
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        Capture(level, area, messageTemplate, propertyValues);
        _inner?.Log(level, area, source, messageTemplate, propertyValues);
    }

    /// <summary>
    /// Returns up to <paramref name="n"/> most-recent captured messages, oldest
    /// first. Pass a non-positive <paramref name="n"/> to get all buffered.
    /// </summary>
    public IEnumerable<string> Recent(int n)
    {
        lock (_gate)
        {
            var take = n <= 0 ? _count : Math.Min(n, _count);
            var result = new string[take];
            // Items are stored oldest..newest across a circular buffer; the
            // newest is at (head-1). We want the last `take`, oldest first.
            var start = (_head - take + _ring.Length * 2) % _ring.Length;
            for (var i = 0; i < take; i++)
                result[i] = _ring[(start + i) % _ring.Length];
            return result;
        }
    }

    /// <summary>Clears the ring buffer.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
        }
    }

    private void Capture(LogEventLevel level, string area, string messageTemplate, object?[]? values)
    {
        if (_bindingOnly && !string.Equals(area, LogArea.Binding, StringComparison.Ordinal))
            return;

        var rendered = Render(messageTemplate, values);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O} [{level}] {area}: {rendered}");

        lock (_gate)
        {
            _ring[_head] = line;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length)
                _count++;
        }
    }

    /// <summary>
    /// Renders a message template by replacing positional <c>{Name}</c> holes
    /// with their values left-to-right. Mirrors how Avalonia's own console sink
    /// substitutes parameters without a full structured-logging engine.
    /// </summary>
    private static string Render(string template, object?[]? values)
    {
        if (values is null || values.Length == 0 || template.IndexOf('{') < 0)
            return template;

        var sb = new System.Text.StringBuilder(template.Length + 16);
        var valueIndex = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close > i)
                {
                    var arg = valueIndex < values.Length ? values[valueIndex] : null;
                    valueIndex++;
                    sb.Append(arg ?? "null");
                    i = close;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
