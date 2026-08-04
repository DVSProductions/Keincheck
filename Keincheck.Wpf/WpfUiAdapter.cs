using Keincheck.Core;

namespace Keincheck.Wpf;

/// <summary>
/// The WPF implementation of the framework-neutral <see cref="IUiAdapter"/>.
/// <para>
/// The interface is implemented across a set of <c>partial</c> files grouped by concern,
/// mirroring the structure of <c>Keincheck.Avalonia.AvaloniaUiAdapter</c>:
/// <list type="bullet">
///   <item><see cref="WpfUiAdapter"/> (this file) — ctor + shared fields/serializer hooks.</item>
///   <item><c>WpfUiAdapter.Topology.cs</c> — roots, top-level, logical/visual children, metadata, bounds, visibility, active-window.</item>
///   <item><c>WpfUiAdapter.Properties.cs</c> — property names, read/write, data context.</item>
///   <item><c>WpfUiAdapter.Visual.cs</c> — <c>RenderTargetBitmap</c> capture + binding-error trace sink.</item>
///   <item><c>WpfUiAdapter.Automation.cs</c> — automation-peer invoke, focus, hit-test.</item>
///   <item><c>WpfUiAdapter.Input.cs</c> — synthetic pointer/wheel/text/keys via Win32 SendInput.</item>
/// </list>
/// Element handles are opaque <see cref="object"/> the adapter casts to WPF's
/// <c>DependencyObject</c>/<c>FrameworkElement</c>/<c>Visual</c> internally, exactly as
/// <c>Keincheck.Avalonia.AvaloniaUiAdapter</c> does for Avalonia.
/// </para>
/// </summary>
public sealed partial class WpfUiAdapter : IUiAdapter
{
    private readonly PropertyValueSerializer _serializer;
    private readonly int _defaultMaxDimension;

    /// <summary>
    /// Constructs the WPF adapter. Signature mirrors
    /// <c>Keincheck.Avalonia.AvaloniaUiAdapter</c> so the WPF <c>UseKeincheckClient</c>
    /// wiring can build it from the same DI singletons (the shared
    /// <see cref="PropertyValueSerializer"/> and screenshot cap) the host registers.
    /// </summary>
    /// <param name="serializer">
    /// Shared property serializer used by the property-read projection. When null, a
    /// default <see cref="PropertyValueSerializer"/> is created.
    /// </param>
    /// <param name="defaultMaxScreenshotDimension">
    /// Fallback max PNG dimension used only when a caller passes a non-positive value to
    /// <see cref="TryRenderToPng"/>.
    /// </param>
    public WpfUiAdapter(
        PropertyValueSerializer? serializer = null,
        int defaultMaxScreenshotDimension = 2048)
    {
        _serializer = serializer ?? new PropertyValueSerializer();
        _defaultMaxDimension = defaultMaxScreenshotDimension > 0 ? defaultMaxScreenshotDimension : 2048;
    }
}
