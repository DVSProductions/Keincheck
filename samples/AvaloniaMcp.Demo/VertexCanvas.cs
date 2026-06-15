using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AvaloniaMcp.Demo;

/// <summary>
/// A small custom-drawn control (analogous to ProtoFace's vertex adorner layer)
/// that paints a set of draggable vertex handles by overriding <see cref="Render"/>
/// and moves them with raw pointer events. It deliberately exposes <b>no</b>
/// automation peer (see <see cref="OnCreateAutomationPeer"/>), so it is the
/// intended target for the synthetic-input fallback path — UI-Automation tools
/// cannot drive it, and a tool must fall back to synthesised pointer input.
/// <para>
/// It also surfaces a couple of plain styled/CLR properties (<see cref="HandleRadius"/>,
/// <see cref="LastDraggedIndex"/>, <see cref="VertexCount"/>) so the property and
/// wait-for tools have a custom-control target to read and write.
/// </para>
/// </summary>
public sealed class VertexCanvas : Control
{
    /// <summary>Radius (px) of each drawn handle. Styled so tools can set it.</summary>
    public static readonly StyledProperty<double> HandleRadiusProperty =
        AvaloniaProperty.Register<VertexCanvas, double>(nameof(HandleRadius), 8.0);

    /// <summary>
    /// Index of the handle most recently moved by a drag, or -1 if none yet.
    /// A direct, readable side-effect a tool can assert against after a
    /// synthetic drag.
    /// </summary>
    public static readonly DirectProperty<VertexCanvas, int> LastDraggedIndexProperty =
        AvaloniaProperty.RegisterDirect<VertexCanvas, int>(
            nameof(LastDraggedIndex),
            o => o.LastDraggedIndex);

    private static readonly IBrush HandleFill =
        new ImmutableSolidColorBrush(Color.FromRgb(0x3D, 0xA9, 0xFC));

    private static readonly IBrush HandleFillActive =
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));

    private static readonly IPen HandlePen =
        new ImmutablePen(new ImmutableSolidColorBrush(Colors.White), 1.5);

    private static readonly IPen EdgePen =
        new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1);

    private static readonly IBrush Backdrop =
        new ImmutableSolidColorBrush(Color.FromRgb(0x22, 0x26, 0x33));

    // The editable vertices, in this control's own coordinate space.
    private readonly List<Point> _points = new()
    {
        new Point(60, 60),
        new Point(180, 50),
        new Point(210, 150),
        new Point(90, 170),
    };

    private int _lastDraggedIndex = -1;
    private int _dragIndex = -1;
    private Point _dragOffset;

    public VertexCanvas()
    {
        // Paint a backdrop in Render so the whole surface receives pointer hits.
        ClipToBounds = true;
        Focusable = true;
    }

    /// <inheritdoc cref="HandleRadiusProperty"/>
    public double HandleRadius
    {
        get => GetValue(HandleRadiusProperty);
        set => SetValue(HandleRadiusProperty, value);
    }

    /// <inheritdoc cref="LastDraggedIndexProperty"/>
    public int LastDraggedIndex
    {
        get => _lastDraggedIndex;
        private set => SetAndRaise(LastDraggedIndexProperty, ref _lastDraggedIndex, value);
    }

    /// <summary>Number of vertices (read-only CLR property; readable by tools).</summary>
    public int VertexCount => _points.Count;

    static VertexCanvas()
    {
        AffectsRender<VertexCanvas>(HandleRadiusProperty);
    }

    /// <summary>
    /// Return <c>null</c> so this control has no UI-Automation peer. This is the
    /// whole point of the control: it makes <c>automation_action</c> report
    /// "no automation peer" and forces the synthetic-input fallback to drive it.
    /// The base signature is non-nullable; <c>null!</c> is the established
    /// Avalonia idiom for "this element exposes no peer".
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => null!;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Backdrop, bounds);

        // Draw connecting edges (closed polygon) so the handles read as a shape.
        if (_points.Count > 1)
        {
            for (var i = 0; i < _points.Count; i++)
            {
                var a = _points[i];
                var b = _points[(i + 1) % _points.Count];
                context.DrawLine(EdgePen, a, b);
            }
        }

        var r = HandleRadius;
        for (var i = 0; i < _points.Count; i++)
        {
            var fill = i == _dragIndex ? HandleFillActive : HandleFill;
            context.DrawEllipse(fill, HandlePen, _points[i], r, r);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var p = e.GetPosition(this);
        var hit = HitTest(p);
        if (hit < 0)
            return;

        _dragIndex = hit;
        _dragOffset = _points[hit] - p; // keep grab offset so the handle doesn't jump
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragIndex < 0)
            return;

        _points[_dragIndex] = e.GetPosition(this) + _dragOffset;
        LastDraggedIndex = _dragIndex;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragIndex < 0)
            return;

        _dragIndex = -1;
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>Index of the topmost handle under <paramref name="p"/>, or -1.</summary>
    private int HitTest(Point p)
    {
        var hit = HandleRadius + 4; // generous pointer target
        var rSq = hit * hit;
        for (var i = _points.Count - 1; i >= 0; i--)
        {
            var d = _points[i] - p;
            if ((d.X * d.X) + (d.Y * d.Y) <= rSq)
                return i;
        }

        return -1;
    }
}
