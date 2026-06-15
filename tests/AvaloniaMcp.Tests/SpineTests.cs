using System.Text.Json;
using AvaloniaMcp.Core;
using Xunit;

namespace AvaloniaMcp.Tests;

/// <summary>
/// Smoke tests for the shared spine. These exercise the parts that do not
/// require a running UI thread (options defaults, JSON coercion, ring buffer).
/// Visual-tree tests belong with the module agents and a headless app session.
/// </summary>
public class SpineTests
{
    [Fact]
    public void Options_Have_Expected_Defaults()
    {
        var opts = new McpServerOptions();
        Assert.Equal(3001, opts.Port);
        Assert.True(opts.MaxScreenshotDimension > 0);
        Assert.True(opts.MaxSerializationDepth > 0);
        Assert.True(opts.CaptureBindingErrors);
    }

    [Fact]
    public void PropertyValueSerializer_Coerces_Int_From_Json()
    {
        var json = JsonDocument.Parse("42").RootElement;
        var ok = PropertyValueSerializer.TryCoerce(json, typeof(int), out var value, out var error);
        Assert.True(ok, error);
        Assert.Equal(42, value);
    }

    [Fact]
    public void PropertyValueSerializer_Coerces_Thickness_Via_TypeConverter()
    {
        var json = JsonDocument.Parse("\"10,5,10,5\"").RootElement;
        var ok = PropertyValueSerializer.TryCoerce(json, typeof(Avalonia.Thickness), out var value, out var error);
        Assert.True(ok, error);
        Assert.Equal(new Avalonia.Thickness(10, 5, 10, 5), value);
    }

    [Fact]
    public void BindingErrorSink_RingBuffer_Returns_Recent_In_Order()
    {
        var sink = new BindingErrorSink(capacity: 3, inner: null, bindingOnly: true);
        for (var i = 0; i < 5; i++)
            sink.Log(Avalonia.Logging.LogEventLevel.Warning, Avalonia.Logging.LogArea.Binding, null, "msg {Index}", i);

        var recent = sink.Recent(10).ToArray();
        Assert.Equal(3, recent.Length);          // bounded to capacity
        Assert.Contains("msg 2", recent[0]);     // oldest retained
        Assert.Contains("msg 4", recent[^1]);    // newest
    }
}
