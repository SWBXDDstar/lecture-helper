using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LectureHelper.Services;

namespace LectureHelper.Annotations;

public enum ToolKind
{
    None, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Mosaic, Blur, Number, Text
}

public sealed class RenderContext
{
    public required EffectCache Effects { get; init; }
}

public abstract class Annotation
{
    public Color Color { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 4;

    /// <summary>
    /// Marker mode: fat and translucent so the screen shows through. It is a property rather than a
    /// separate shape type, so Shift still gives a straight highlight and Ctrl a boxed one.
    /// </summary>
    public bool Highlight { get; set; }

    protected const byte HighlightAlpha = 0x66;
    protected const double HighlightWidth = 3;

    public abstract void Render(DrawingContext dc, RenderContext ctx);

    /// <summary>False for degenerate shapes (a click that never became a drag).</summary>
    public virtual bool IsValid => true;

    protected Color EffectiveColor => Highlight
        ? Color.FromArgb(HighlightAlpha, Color.R, Color.G, Color.B)
        : Color;

    protected double EffectiveThickness => Highlight ? Thickness * HighlightWidth : Thickness;

    protected Pen CreatePen(double? thickness = null, PenLineCap cap = PenLineCap.Round)
    {
        if (Highlight) cap = PenLineCap.Square;

        var pen = new Pen(new SolidColorBrush(EffectiveColor), thickness ?? EffectiveThickness)
        {
            StartLineCap = cap,
            EndLineCap = cap,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }
}

public sealed class PenAnnotation : Annotation
{
    public List<Point> Points { get; } = new();

    public override bool IsValid => Points.Count > 1;

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        if (Points.Count == 0) return;

        if (Points.Count == 1)
        {
            double r = EffectiveThickness / 2;
            dc.DrawEllipse(new SolidColorBrush(EffectiveColor), null, Points[0], r, r);
            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(Points[0], false, false);
            g.PolyLineTo(Points.GetRange(1, Points.Count - 1), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, CreatePen(), geometry);
    }
}

public abstract class TwoPointAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }

    public Rect Bounds => new(
        Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
        Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));

    public override bool IsValid => (End - Start).Length > 3;
}

public sealed class LineAnnotation : TwoPointAnnotation
{
    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawLine(CreatePen(), Start, End);
}

public sealed class ArrowAnnotation : TwoPointAnnotation
{
    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var delta = End - Start;
        double length = delta.Length;
        if (length < 1) return;

        double head = Math.Max(EffectiveThickness * 4, 12);
        head = Math.Min(head, length * 0.6);
        var dir = delta / length;
        var normal = new Vector(-dir.Y, dir.X);
        Point baseCenter = End - dir * head;

        dc.DrawLine(CreatePen(), Start, baseCenter);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(End, true, true);
            g.LineTo(baseCenter + normal * head * 0.45, true, true);
            g.LineTo(baseCenter - normal * head * 0.45, true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(new SolidColorBrush(EffectiveColor), null, geometry);
    }
}

public sealed class RectangleAnnotation : TwoPointAnnotation
{
    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawRectangle(null, CreatePen(cap: PenLineCap.Flat), Bounds);
}

public sealed class EllipseAnnotation : TwoPointAnnotation
{
    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var b = Bounds;
        dc.DrawEllipse(null, CreatePen(cap: PenLineCap.Flat),
            new Point(b.X + b.Width / 2, b.Y + b.Height / 2), b.Width / 2, b.Height / 2);
    }
}

public sealed class EffectAnnotation : TwoPointAnnotation
{
    public EffectKind Kind { get; set; } = EffectKind.Mosaic;
    public int Strength { get; set; } = 12;

    public override bool IsValid => Bounds.Width > 2 && Bounds.Height > 2;

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        // Only this rectangle is processed, so the cost tracks the area drawn, not the canvas size.
        var bitmap = ctx.Effects.Get(Bounds, Kind, Strength);
        if (bitmap is null) return;

        dc.DrawImage(bitmap, ctx.Effects.ClampedArea(Bounds));
    }
}

public sealed class NumberAnnotation : Annotation
{
    public Point Center { get; set; }
    public int Value { get; set; } = 1;

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        // Deliberately opaque: a translucent step number is unreadable against busy slides.
        double radius = Math.Max(Thickness * 2.4, 11);
        dc.DrawEllipse(new SolidColorBrush(Color), null, Center, radius, radius);

        var text = new FormattedText(
            Value.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            radius * 1.25, Brushes.White, 96);

        dc.DrawText(text, new Point(Center.X - text.Width / 2, Center.Y - text.Height / 2));
    }
}

public sealed class TextAnnotation : Annotation
{
    public Point Origin { get; set; }
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 20;

    public override bool IsValid => !string.IsNullOrWhiteSpace(Text);

    public FormattedText Build() => new(
        Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
        FontSize, new SolidColorBrush(Color), 96);

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        if (string.IsNullOrEmpty(Text)) return;
        dc.DrawText(Build(), Origin);
    }
}
