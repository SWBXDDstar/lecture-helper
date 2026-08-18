using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
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

    public AnnotationLayer() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        if (Context is null || _selection.IsEmpty) return;

        foreach (var item in Items)
            item.Render(dc, Context);

        Preview?.Render(dc, Context);

        if (CursorPreviewAt is { } point && CursorPreviewSize > 0)
        {
            double r = CursorPreviewSize / 2;
            var outline = new Pen(Brushes.White, 1);
            outline.Freeze();
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x88, CursorPreviewColor.R, CursorPreviewColor.G, CursorPreviewColor.B)), outline, point, r, r);
        }
    }
}
