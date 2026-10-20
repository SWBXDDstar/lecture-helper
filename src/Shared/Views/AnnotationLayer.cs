using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LectureHelper.Annotations;

namespace LectureHelper.Views;

/// <summary>Draws every committed annotation plus the in-progress one, clipped to the selection.</summary>
public sealed class AnnotationLayer : FrameworkElement
{
    public List<Annotation> Items { get; } = new();
    public Annotation? Preview { get; set; }
    public RenderContext? Context { get; set; }

    private Rect _selection = Rect.Empty;
    public Rect Selection
    {
        get => _selection;
        set { _selection = value; Clip = value.IsEmpty ? null : new RectangleGeometry(value); InvalidateVisual(); }
    }

    public Point? CursorPreviewAt { get; set; }
    public Color CursorPreviewColor { get; set; } = Colors.Red;
    public double CursorPreviewSize { get; set; }

    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    public AnnotationLayer()
    {
        IsHitTestVisible = false;
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            CursorPreviewAt = null;
            InvalidateVisual();
        };
    }

    /// <summary>
    /// Shows the pen's size as a circle at the cursor, then lets it vanish by itself. It only appears
    /// right after the user turns the wheel, so nothing is on a presentation slide unasked.
    /// </summary>
    public void FlashSize(Point at, Color color, double diameter)
    {
        CursorPreviewAt = at;
        CursorPreviewColor = color;
        CursorPreviewSize = diameter;
        _flashTimer.Stop();
        _flashTimer.Start();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Context is null || _selection.IsEmpty) return;

        foreach (var item in Items)
            item.Render(dc, Context);

        Preview?.Render(dc, Context);

        if (CursorPreviewAt is { } point && CursorPreviewSize > 0)
        {
            // Thin pens make a dot of a pixel or two, so the rings keep it findable on any background.
            double r = Math.Max(CursorPreviewSize / 2, 1);
            var dark = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 1);
            var white = new Pen(Brushes.White, 1);
            dark.Freeze();
            white.Freeze();
            dc.DrawEllipse(null, dark, point, r + 1, r + 1);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xB0, CursorPreviewColor.R, CursorPreviewColor.G, CursorPreviewColor.B)), white, point, r, r);
        }
    }
}
