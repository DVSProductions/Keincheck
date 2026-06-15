namespace Keincheck.Core;

/// <summary>
/// Configuration for the embedded MCP server. All values have sane defaults;
/// override them via <see cref="AppBuilderExtensions.UseMcpServer"/>.
/// </summary>
/// <remarks>
/// This is the Keincheck options type. It is intentionally named the same as
/// the MCP library's own options but lives in the <c>Keincheck</c> namespace,
/// so always refer to it unqualified inside this library.
/// </remarks>
public sealed class McpServerOptions
{
    /// <summary>
    /// TCP port the Kestrel/MCP host listens on. Bound to 127.0.0.1 only.
    /// Default 3001.
    /// </summary>
    public int Port { get; set; } = 3001;

    /// <summary>
    /// Maximum width or height (in pixels) of a captured screenshot. Larger
    /// visuals are downscaled to fit. Default 2048.
    /// </summary>
    public int MaxScreenshotDimension { get; set; } = 2048;

    /// <summary>
    /// Maximum recursion depth when serializing the visual/logical tree or
    /// nested property values. Guards against cycles and runaway payloads.
    /// Default 25.
    /// </summary>
    public int MaxSerializationDepth { get; set; } = 25;

    /// <summary>
    /// Number of recent binding/log errors retained in the in-memory ring
    /// buffer exposed by <see cref="BindingErrorSink"/>. Default 256.
    /// </summary>
    public int BindingErrorBufferSize { get; set; } = 256;

    /// <summary>
    /// When <c>true</c> (default), the host installs a <see cref="BindingErrorSink"/>
    /// as the Avalonia logging sink to capture binding errors. Set to
    /// <c>false</c> to leave the existing <c>Logger.Sink</c> untouched.
    /// </summary>
    public bool CaptureBindingErrors { get; set; } = true;
}
