using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Hisui.Pdf.App.Controls;

/// <summary>
/// A freehand drawing surface for capturing a signature. Strokes are collected from pointer input and
/// painted as round-capped polylines on a transparent background, so rendering the control to a
/// <c>RenderTargetBitmap</c> yields a transparent PNG of just the ink.
/// </summary>
public sealed class SignaturePad : Control
{
    private readonly List<List<Point>> _strokes = [];
    private List<Point>? _current;

    /// <summary>True once anything has been drawn. A stroke list is only created on pointer-press, and
    /// Render paints even a single-point tap as a dot, so any stroke counts as ink.</summary>
    public bool HasInk => _strokes.Count > 0;

    public void Clear()
    {
        _strokes.Clear();
        _current = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _current = [e.GetPosition(this)];
        _strokes.Add(_current);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_current is null) return;
        _current.Add(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _current = null;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        // Fill with a transparent brush so the whole surface is hit-testable (a control with no drawn
        // background does not receive pointer events on empty pixels), without adding opaque pixels.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var pen = new Pen(Brushes.Black, 2.5,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        foreach (var stroke in _strokes)
        {
            if (stroke.Count == 1)
            {
                // A single tap: render a dot so it isn't lost.
                context.DrawEllipse(Brushes.Black, null, stroke[0], 1.25, 1.25);
                continue;
            }
            for (var i = 1; i < stroke.Count; i++)
                context.DrawLine(pen, stroke[i - 1], stroke[i]);
        }
    }
}
