using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LectureHelper.Annotations;

public enum ShapeKind { Pen, Rectangle, Ellipse, Line, Arrow }

/// <summary>
/// The single source of truth for drawing input. ScreenNote and ScreenCapture both consume it, so the
/// same keys produce the same shape in either program and the muscle memory carries across.
/// </summary>
public static class DrawingShortcuts
{
    public const double MinThickness = 1;
    public const double MaxThickness = 60;
    public const double DefaultThickness = 4;

    /// <summary>
    /// ZoomIt-style modifier drag: nothing = pen, Ctrl = rectangle, Tab = ellipse,
    /// Shift = line, Ctrl+Shift = arrow.
    /// </summary>
    public static ShapeKind ShapeFor(ModifierKeys modifiers, bool tabHeld)
    {
        if (tabHeld) return ShapeKind.Ellipse;

        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;

        return (ctrl, shift) switch
        {
            (true, true) => ShapeKind.Arrow,
            (true, false) => ShapeKind.Rectangle,
            (false, true) => ShapeKind.Line,
            _ => ShapeKind.Pen,
        };
    }

    public static ShapeKind ShapeFromKeyboard() =>
        ShapeFor(Keyboard.Modifiers, Keyboard.IsKeyDown(Key.Tab));

    public static Annotation Create(ShapeKind kind, Point origin, Color color, double thickness, bool highlight = false)
    {
        Annotation shape = kind switch
        {
            ShapeKind.Rectangle => new RectangleAnnotation { Start = origin, End = origin },
            ShapeKind.Ellipse => new EllipseAnnotation { Start = origin, End = origin },
            ShapeKind.Line => new LineAnnotation { Start = origin, End = origin },
            // The head lands where the drag ends, the way Lightshot does it - not at the origin like ZoomIt.
            ShapeKind.Arrow => new ArrowAnnotation { Start = origin, End = origin },
            _ => NewPen(origin),
        };

        shape.Color = color;
        shape.Thickness = thickness;
        shape.Highlight = highlight;
        return shape;
    }

    private static PenAnnotation NewPen(Point origin)
    {
        var pen = new PenAnnotation();
        pen.Points.Add(origin);
        return pen;
    }

    /// <summary>Extends an in-progress shape to the current cursor position.</summary>
    public static void Extend(Annotation shape, Point point)
    {
        switch (shape)
        {
            case PenAnnotation pen: pen.Points.Add(point); break;
            case EffectAnnotation effect: effect.End = point; break;
            case TwoPointAnnotation twoPoint: twoPoint.End = point; break;
        }
    }

    /// <summary>
    /// The letter keys both programs share. Shapes live on the drag modifiers, so the letters are
    /// free for the tools that change *what* is being drawn.
    /// </summary>
    public static ToolKind? ToolFor(Key key, bool shift) => key switch
    {
        Key.H => ToolKind.Highlighter,
        Key.M when shift => ToolKind.Blur,
        Key.M => ToolKind.Mosaic,
        Key.N => ToolKind.Number,
        Key.T => ToolKind.Text,
        // Q S V X O 는 채점 도장이었는데 수업에서 거의 안 써서 뺐다 (26.08.23).
        // X는 오버레이 나가기로, O는 원래 자리였던 주황색으로 돌아갔다.
        _ => null,
    };

    /// <summary>Tools that drop a mark on a single click instead of being dragged out.</summary>
    public static bool IsStamp(ToolKind tool) => tool is ToolKind.Number;

    public static string ToolLabel(ToolKind tool) => tool switch
    {
        ToolKind.Highlighter => "형광펜",
        ToolKind.Mosaic => "모자이크",
        ToolKind.Blur => "흐리게",
        ToolKind.Number => "숫자",
        ToolKind.Text => "텍스트",
        _ => "펜",
    };

    /// <summary>ZoomIt's colour letters.</summary>
    public static Color? ColorFor(Key key) => key switch
    {
        Key.R => Color.FromRgb(0xF4, 0x43, 0x36),
        Key.G => Color.FromRgb(0x00, 0xC8, 0x53),
        Key.B => Color.FromRgb(0x29, 0x62, 0xFF),
        Key.Y => Color.FromRgb(0xFF, 0xD4, 0x00),
        Key.P => Color.FromRgb(0xFF, 0x40, 0x81),
        Key.O => Color.FromRgb(0xFF, 0x7A, 0x00),
        _ => null,
    };

    public static double AdjustThickness(double current, int wheelSteps) =>
        Math.Clamp(current + wheelSteps, MinThickness, MaxThickness);

    public static string ShapeLabel(ShapeKind kind) => kind switch
    {
        ShapeKind.Rectangle => "네모",
        ShapeKind.Ellipse => "동그라미",
        ShapeKind.Line => "직선",
        ShapeKind.Arrow => "화살표",
        _ => "펜",
    };
}
