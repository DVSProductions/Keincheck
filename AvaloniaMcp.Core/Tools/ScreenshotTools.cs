using System.ComponentModel;
using System.Text.Json;
using Avalonia.Controls;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaMcp.Core.Tools;

/// <summary>
/// MCP tools that capture PNG screenshots of the live Avalonia UI and return
/// them as MCP image content (base64 PNG).
/// </summary>
/// <remarks>
/// All framework-specific rendering is performed by <see cref="IUiAdapter"/>
/// (<see cref="IUiAdapter.TryRenderControlToPng"/> /
/// <see cref="IUiAdapter.TryRenderVisualToPng"/>), which runs on the UI thread; the
/// tool bodies only resolve targets (via <see cref="ControlRegistry"/>) and marshal
/// onto the UI thread via <see cref="UiDispatch"/>. Each tool returns a
/// <see cref="ContentBlock"/>: an <see cref="ImageContentBlock"/> on success, or a
/// <see cref="TextContentBlock"/> carrying a structured JSON error object on
/// failure (so the convention "never throw raw on a bad handle/selector" holds
/// while still emitting a well-typed MCP content block).
/// </remarks>
[McpServerToolType]
public static class ScreenshotTools
{
    private const string PngMimeType = "image/png";

    /// <summary>
    /// Renders a top-level window to a PNG and returns it as image content.
    /// </summary>
    [McpServerTool, Description(
        "Render a window to a PNG screenshot and return it as image content. " +
        "Optionally target a specific window via a control handle (e.g. \"ctl-1a\") " +
        "or a CSS-ish selector (e.g. \"Window[Name=main]\"); when omitted, the first " +
        "open top-level window is captured.")]
    public static Task<ContentBlock> ScreenshotWindow(
        ControlRegistry registry,
        IUiAdapter ui,
        McpServerOptions options,
        [Description("Optional control handle or selector identifying a window (or any control inside it). " +
                     "If omitted, the first open top-level window is used.")]
        string? target = null)
        => UiDispatch.Run(() =>
        {
            // Resolve the TopLevel to render.
            if (!TryResolveTopLevel(registry, ui, target, out var topLevel, out var resolveError))
                return Error(resolveError);

            if (!ui.TryRenderVisualToPng(topLevel!, options.MaxScreenshotDimension, cropRect: null, out var png, out var renderError))
                return Error(renderError);

            return Image(png);
        });

    /// <summary>
    /// Renders a single control's subtree to a PNG and returns it as image content.
    /// </summary>
    [McpServerTool, Description(
        "Render a single control (and its descendants) to a PNG screenshot and return " +
        "it as image content. Address the control by handle (e.g. \"ctl-1a\") or by a " +
        "CSS-ish selector (e.g. \"Button[Name=ok]\"); a selector matching multiple " +
        "controls captures the first match in document order.")]
    public static Task<ContentBlock> ScreenshotControl(
        ControlRegistry registry,
        IUiAdapter ui,
        McpServerOptions options,
        [Description("Control handle (\"ctl-1a\") or CSS-ish selector (\"Button[Name=ok]\").")]
        string target)
        => UiDispatch.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(target))
                return Error("A control handle or selector is required.");

            if (!TryResolveControl(registry, target, out var control, out var resolveError))
                return Error(resolveError);

            // The adapter renders the control subtree directly, falling back to a
            // cropped TopLevel render when a direct render is not usable.
            if (!ui.TryRenderControlToPng(control!, options.MaxScreenshotDimension, out var png, out var renderError))
                return Error(renderError);

            return Image(png);
        });

    // ---- resolution helpers ------------------------------------------------

    private static bool TryResolveControl(
        ControlRegistry registry, string target, out Control? control, out string error)
    {
        // Handle first, then selector (the standard "handle if it resolves, else selector").
        if (registry.TryResolve(target, out control) && control is not null)
        {
            error = string.Empty;
            return true;
        }

        var matches = registry.Query(target);
        if (matches.Count > 0)
        {
            control = matches[0];
            error = string.Empty;
            return true;
        }

        control = null;
        error = $"No control found for handle or selector '{target}'.";
        return false;
    }

    private static bool TryResolveTopLevel(
        ControlRegistry registry, IUiAdapter ui, string? target, out TopLevel? topLevel, out string error)
    {
        topLevel = null;
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(target))
        {
            if (!TryResolveControl(registry, target!, out var control, out error))
                return false;

            // A resolved TopLevel/Window is its own root; otherwise climb to its host.
            topLevel = control as TopLevel ?? ui.GetTopLevel(control!);
            if (topLevel is null)
            {
                error = $"Control '{Describe(ui, control!)}' is not inside any open window.";
                return false;
            }

            return true;
        }

        // No target: capture the first open top-level window.
        topLevel = ui.EnumerateRoots().OfType<TopLevel>().FirstOrDefault();
        if (topLevel is null)
        {
            error = "No open top-level window to capture.";
            return false;
        }

        return true;
    }

    // ---- misc --------------------------------------------------------------

    private static string Describe(IUiAdapter ui, Control control)
    {
        var name = ui.GetName(control);
        var typeName = ui.GetTypeName(control);
        return string.IsNullOrEmpty(name) ? typeName : $"{typeName}#{name}";
    }

    /// <summary>Wraps already-encoded PNG bytes in MCP image content.</summary>
    private static ContentBlock Image(byte[] png) =>
        ImageContentBlock.FromBytes(png, PngMimeType);

    /// <summary>
    /// Produces a structured error as a <see cref="TextContentBlock"/> so the tool's
    /// declared <see cref="ContentBlock"/> return type is honored without throwing.
    /// </summary>
    private static ContentBlock Error(string message) =>
        new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new { ok = false, error = message }),
        };
}
