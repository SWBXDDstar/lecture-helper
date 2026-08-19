using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LectureHelper.Services;

namespace LectureHelper.Annotations;

public enum ToolKind
{
    None, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Mosaic, Blur, Number, Text,
    Question, Star, Check, Cross, Circle
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

public enum StampKind { Question, Star, Check, Cross, Circle }

/// <summary>
/// A one-click mark. Drawn opaque with a white rim so it stays legible over slides, photos and
/// dark backgrounds alike — these get stamped onto whatever happens to be on screen.
/// </summary>
public sealed class StampAnnotation : Annotation
{
    public Point Center { get; set; }
    public StampKind Kind { get; set; }

    private double Radius => Math.Max(Thickness * 3.2, 14);

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var fill = new SolidColorBrush(Color);
        var rim = new Pen(Brushes.White, Math.Max(Radius * 0.14, 2));
        rim.Freeze();

        switch (Kind)
        {
            case StampKind.Star: RenderStar(dc, fill, rim); break;
            case StampKind.Check: RenderCheck(dc, fill, rim); break;
            case StampKind.Cross: RenderCross(dc, fill, rim); break;
            // 채점의 ○는 속이 빈 고리로 그린다 — 가린 자리 아래 글씨가 그대로 보여야 한다.
            case StampKind.Circle: RenderCircle(dc); break;
            default: RenderGlyph(dc, fill, rim, "?"); break;
        }
    }

    private void RenderStar(DrawingContext dc, Brush fill, Pen rim)
    {
        double outer = Radius;
        double inner = outer * 0.42;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (int i = 0; i < 10; i++)
            {
                // Start at -90 degrees so a point faces up.
                double angle = -Math.PI / 2 + i * Math.PI / 5;
                double r = i % 2 == 0 ? outer : inner;
                var p = new Point(Center.X + Math.Cos(angle) * r, Center.Y + Math.Sin(angle) * r);

                if (i == 0) g.BeginFigure(p, true, true);
                else g.LineTo(p, true, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(fill, rim, geometry);
    }

    /// <summary>X 표시. 체크와 짝이라 같은 원 안에 흰 획으로 그린다.</summary>
    private void RenderCross(DrawingContext dc, Brush fill, Pen rim)
    {
        dc.DrawEllipse(fill, rim, Center, Radius, Radius);

        var stroke = new Pen(Brushes.White, Radius * 0.30)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        stroke.Freeze();

        double d = Radius * 0.40;
        dc.DrawLine(stroke, new Point(Center.X - d, Center.Y - d), new Point(Center.X + d, Center.Y + d));
        dc.DrawLine(stroke, new Point(Center.X + d, Center.Y - d), new Point(Center.X - d, Center.Y + d));
    }

    /// <summary>
    /// 채점용 ○. 다른 도장과 달리 속을 채우지 않는다 — 정답에 동그라미를 치면
    /// 그 안의 글씨가 보여야 하기 때문이다. 흰 테두리를 바깥에 덧대 어두운 배경에서도 보이게 한다.
    /// </summary>
    private void RenderCircle(DrawingContext dc)
    {
        double width = Radius * 0.34;

        var halo = new Pen(Brushes.White, width * 1.7);
        halo.Freeze();
        dc.DrawEllipse(null, halo, Center, Radius * 0.82, Radius * 0.82);

        var ring = new Pen(new SolidColorBrush(Color), width);
        ring.Freeze();
        dc.DrawEllipse(null, ring, Center, Radius * 0.82, Radius * 0.82);
    }

    private void RenderGlyph(DrawingContext dc, Brush fill, Pen rim, string glyph)
    {
        dc.DrawEllipse(fill, rim, Center, Radius, Radius);

        var text = new FormattedText(
            glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Radius * 1.6, Brushes.White, 96);

        dc.DrawText(text, new Point(Center.X - text.Width / 2, Center.Y - text.Height / 2));
    }

    /// <summary>A tick drawn as strokes rather than a glyph — fonts render ✓ inconsistently.</summary>
    private void RenderCheck(DrawingContext dc, Brush fill, Pen rim)
    {
        dc.DrawEllipse(fill, rim, Center, Radius, Radius);

        var tick = new Pen(Brushes.White, Radius * 0.30)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        tick.Freeze();

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(Center.X - Radius * 0.42, Center.Y + Radius * 0.02), false, false);
            g.LineTo(new Point(Center.X - Radius * 0.12, Center.Y + Radius * 0.36), true, true);
            g.LineTo(new Point(Center.X + Radius * 0.46, Center.Y - Radius * 0.36), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, tick, geometry);
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
