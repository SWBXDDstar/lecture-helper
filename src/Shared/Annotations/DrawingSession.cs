using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LectureHelper.Services;

namespace LectureHelper.Annotations;

/// <summary>
/// The drawing state ScreenNote's two overlays share: current tool, colour, thickness, the stroke in
/// progress and the undo stack. Keeping it here means the zoom view and the annotation view cannot
/// drift apart in behaviour.
/// </summary>
public sealed class DrawingSession
{
    private readonly List<Annotation> _items;
    private readonly Stack<Annotation> _redo = new();

    public DrawingSession(List<Annotation> items) => _items = items;

    public Color Color { get; private set; } = Color.FromRgb(0xF4, 0x43, 0x36);
    public double Thickness { get; private set; } = DrawingShortcuts.DefaultThickness;
    public double FontSize { get; private set; } = 28;
    public int EffectStrength { get; private set; } = 12;
    public ToolKind Tool { get; private set; } = ToolKind.None;

    public Annotation? Current { get; private set; }

    public bool IsTextMode => Tool == ToolKind.Text;
    public bool IsEffectTool => Tool is ToolKind.Mosaic or ToolKind.Blur;

    /// <summary>Toggles a tool off when it is already active, so the same key both enters and leaves.</summary>
    public void SetTool(ToolKind tool) => Tool = Tool == tool ? ToolKind.None : tool;

    public void SetColor(Color color)
    {
        Color = color;
        if (Current is not null) Current.Color = color;
    }

    /// <summary>True when the wheel is changing a pen width (not effect strength or font size).</summary>
    public bool WheelIsThickness => Current is not EffectAnnotation && !IsEffectTool && !IsTextMode;

    /// <summary>Width of the mark the pen leaves; the highlighter is drawn three times as wide.</summary>
    public double BrushDiameter => Tool == ToolKind.Highlighter ? Thickness * 3 : Thickness;

    /// <summary>The wheel means thickness, effect strength or font size depending on the tool.</summary>
    public string Adjust(int steps)
    {
        if (Current is EffectAnnotation effect)
        {
            EffectStrength = Math.Clamp(EffectStrength + steps * 2, 4, 60);
            effect.Strength = EffectStrength;
            return $"세기 {EffectStrength}";
        }

        if (IsEffectTool)
        {
            EffectStrength = Math.Clamp(EffectStrength + steps * 2, 4, 60);
            return $"세기 {EffectStrength}";
        }

        if (IsTextMode)
        {
            FontSize = Math.Clamp(FontSize + steps * 2, 10, 160);
            return $"글자 {FontSize:0}";
        }

        Thickness = DrawingShortcuts.AdjustThickness(Thickness, steps);
        if (Current is not null) Current.Thickness = Thickness;
        return $"두께 {Thickness:0}";
    }

    /// <summary>
    /// Begins a stroke. Returns false for tools that commit on a single click instead of a drag, so
    /// the caller knows not to start a drag.
    /// </summary>
    public bool Begin(Point origin)
    {
        if (DrawingShortcuts.IsStamp(Tool))
        {
            Add(new NumberAnnotation
            {
                Center = origin,
                Value = _items.OfType<NumberAnnotation>().Count() + 1,
                Color = Color,
                Thickness = Thickness,
            });
            return false;
        }

        Current = Tool switch
        {
            ToolKind.Mosaic => new EffectAnnotation { Start = origin, End = origin, Kind = EffectKind.Mosaic, Strength = EffectStrength },
            ToolKind.Blur => new EffectAnnotation { Start = origin, End = origin, Kind = EffectKind.Blur, Strength = EffectStrength },
            // The highlighter honours the drag modifiers too, so Shift gives a straight highlight.
            _ => DrawingShortcuts.Create(DrawingShortcuts.ShapeFromKeyboard(), origin, Color, Thickness,
                    Tool == ToolKind.Highlighter),
        };
        return true;
    }

    public void Extend(Point point)
    {
        if (Current is not null) DrawingShortcuts.Extend(Current, point);
    }

    public void Commit()
    {
        if (Current is { IsValid: true }) Add(Current);
        Current = null;
    }

    public void Cancel() => Current = null;

    public void AddText(Point origin, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Add(new TextAnnotation { Origin = origin, Text = text, FontSize = FontSize, Color = Color });
    }

    public void Undo()
    {
        if (_items.Count == 0) return;

        _redo.Push(_items[^1]);
        _items.RemoveAt(_items.Count - 1);
    }

    public void Redo()
    {
        if (_redo.Count > 0) _items.Add(_redo.Pop());
    }

    public void EraseAll()
    {
        for (int i = _items.Count - 1; i >= 0; i--) _redo.Push(_items[i]);
        _items.Clear();
    }

    private void Add(Annotation annotation)
    {
        _items.Add(annotation);
        _redo.Clear();
    }

    /// <summary>Handles the shared colour and tool letters. False when the key means nothing here.</summary>
    public bool HandleKey(Key key, bool shift, out string? note)
    {
        note = null;

        if (DrawingShortcuts.ColorFor(key) is { } color)
        {
            SetColor(color);
            return true;
        }

        if (DrawingShortcuts.ToolFor(key, shift) is { } tool)
        {
            SetTool(tool);
            note = Tool == ToolKind.None ? "펜" : DrawingShortcuts.ToolLabel(tool);
            return true;
        }

        return false;
    }

    /// <summary>F1 안내 맨 윗줄에 뜨는 한 줄. 저절로는 안 뜨고 불렀을 때만 보인다.</summary>
    public string StatusText => Tool switch
    {
        ToolKind.None => $"두께 {Thickness:0}",
        ToolKind.Text => $"텍스트 · 글자 {FontSize:0}",
        ToolKind.Mosaic or ToolKind.Blur => $"{DrawingShortcuts.ToolLabel(Tool)} · 세기 {EffectStrength}",
        _ => $"{DrawingShortcuts.ToolLabel(Tool)} · 두께 {Thickness:0}",
    };
}
