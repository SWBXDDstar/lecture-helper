using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using LectureHelper.Annotations;
using LectureHelper.Native;
using LectureHelper.Services;
using Shapes = System.Windows.Shapes;

namespace ScreenCapture.Views;

public partial class CaptureWindow : Window
{
    private const double HandleSize = 8;
    private const double HandleHitRadius = 9;
    private const double MinSelection = 4;

    private static readonly string[] Palette =
    {
        "#FFF44336", "#FFFF7A00", "#FFFFD400", "#FF00C853",
        "#FF00B0FF", "#FF2962FF", "#FFAA00FF", "#FFFF4081",
        "#FFFFFFFF", "#FF9E9E9E", "#FF000000", "#FF795548",
        "#FF00BFA5", "#FFC51162", "#FF304FFE", "#FF64DD17",
    };

    private enum DragMode { None, Creating, Moving, Resizing, Drawing }

    private readonly BitmapSource _shot;
    private readonly NativeMethods.RECT _bounds;
    private readonly EffectCache _effects;
    private readonly RenderContext _context;
    private readonly Shapes.Rectangle[] _handles = new Shapes.Rectangle[8];
    private readonly Stack<Annotation> _redo = new();
    private readonly System.Windows.Threading.DispatcherTimer _hintTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };

    private Rect _selection = Rect.Empty;
    private bool _hasSelection;

    private DragMode _drag = DragMode.None;
    private Point _dragOrigin;
    private Rect _dragStartRect;
    private int _activeHandle = -1;
    private bool _dragMoved;
    private Rect? _restoreSelection;

    private ToolKind _tool = ToolKind.None;
    private Color _color = Colors.Red;
    private double _thickness = 4;
    private int _effectStrength = 12;
    private double _fontSize = 22;

    private Annotation? _current;
    private Rect? _hoverWindow;
    private Point _cursor;
    private Point _textOrigin;
    private bool _closing;
    private bool _activated;

    public CaptureWindow()
    {
        InitializeComponent();

        _shot = ScreenGrabber.CaptureVirtualScreen(out _bounds);
        _effects = new EffectCache(_shot);
        _context = new RenderContext { Effects = _effects };

        Shot.Source = _shot;
        Shot.Width = _shot.PixelWidth;
        Shot.Height = _shot.PixelHeight;

        Surface.Width = _shot.PixelWidth;
        Surface.Height = _shot.PixelHeight;

        Layer.Width = _shot.PixelWidth;
        Layer.Height = _shot.PixelHeight;
        Layer.Context = _context;

        BuildHandles();
        BuildPalette();
        WireToolbar();

        Mask.Data = new RectangleGeometry(new Rect(0, 0, _shot.PixelWidth, _shot.PixelHeight));
        UpdateSizeReadout();
        UpdateUndoRedoState();

        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            if (_hasSelection) HintBar.Visibility = Visibility.Collapsed;
        };
    }

    // ---------------------------------------------------------------- setup

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(handle, IntPtr.Zero,
            _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);

        // Work in physical pixels everywhere inside the overlay so mixed-DPI monitors line up 1:1.
        var dpi = VisualTreeHelper.GetDpi(this);
        Root.LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);

        NativeMethods.SetForegroundWindow(handle);
        Activate();
        Focus();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PositionHintBar();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _activated = true;
    }

    private void BuildHandles()
    {
        for (int i = 0; i < _handles.Length; i++)
        {
            var rect = new Shapes.Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
            };
            _handles[i] = rect;
            Handles.Children.Add(rect);
        }
    }

    private void BuildPalette()
    {
        foreach (string hex in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Shapes.Rectangle
            {
                Width = 30,
                Height = 24,
                RadiusX = 3,
                RadiusY = 3,
                Margin = new Thickness(3),
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x7D)),
                StrokeThickness = 1,
                Cursor = Cursors.Hand,
            };
            swatch.MouseLeftButtonDown += (_, args) =>
            {
                args.Handled = true;
                SetColor(color);
                ColorPalette.Visibility = Visibility.Collapsed;
            };
            ColorGrid.Children.Add(swatch);
        }
    }

    private void WireToolbar()
    {
        BtnPen.Click += (_, _) => SetTool(ToolKind.Pen);
        BtnHighlighter.Click += (_, _) => SetTool(ToolKind.Highlighter);
        BtnLine.Click += (_, _) => SetTool(ToolKind.Line);
        BtnArrow.Click += (_, _) => SetTool(ToolKind.Arrow);
        BtnRect.Click += (_, _) => SetTool(ToolKind.Rectangle);
        BtnEllipse.Click += (_, _) => SetTool(ToolKind.Ellipse);
        BtnMosaic.Click += (_, _) => SetTool(ToolKind.Mosaic);
        BtnBlur.Click += (_, _) => SetTool(ToolKind.Blur);
        BtnNumber.Click += (_, _) => SetTool(ToolKind.Number);
        BtnQuestion.Click += (_, _) => SetTool(ToolKind.Question);
        BtnStar.Click += (_, _) => SetTool(ToolKind.Star);
        BtnCheck.Click += (_, _) => SetTool(ToolKind.Check);
        BtnText.Click += (_, _) => SetTool(ToolKind.Text);

        BtnColor.Click += (_, _) =>
        {
            ColorPalette.Visibility = ColorPalette.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (ColorPalette.Visibility == Visibility.Visible) PositionPalette();
        };

        BtnUndo.Click += (_, _) => Undo();
        BtnRedo.Click += (_, _) => Redo();
        BtnPin.Click += (_, _) => FinishPin();
        BtnCopy.Click += (_, _) => FinishCopy();
        BtnSave.Click += (_, _) => FinishSave();
        BtnClose.Click += (_, _) => Close();

        TextEditor.PreviewKeyDown += TextEditorOnPreviewKeyDown;
        TextEditor.LostKeyboardFocus += (_, _) => CommitText();
    }

    // ---------------------------------------------------------------- tools

    private void SetTool(ToolKind tool)
    {
        CommitText();
        _tool = _tool == tool ? ToolKind.None : tool;

        BtnPen.IsChecked = _tool == ToolKind.Pen;
        BtnHighlighter.IsChecked = _tool == ToolKind.Highlighter;
        BtnLine.IsChecked = _tool == ToolKind.Line;
        BtnArrow.IsChecked = _tool == ToolKind.Arrow;
        BtnRect.IsChecked = _tool == ToolKind.Rectangle;
        BtnEllipse.IsChecked = _tool == ToolKind.Ellipse;
        BtnMosaic.IsChecked = _tool == ToolKind.Mosaic;
        BtnBlur.IsChecked = _tool == ToolKind.Blur;
        BtnNumber.IsChecked = _tool == ToolKind.Number;
        BtnQuestion.IsChecked = _tool == ToolKind.Question;
        BtnStar.IsChecked = _tool == ToolKind.Star;
        BtnCheck.IsChecked = _tool == ToolKind.Check;
        BtnText.IsChecked = _tool == ToolKind.Text;

        ColorPalette.Visibility = Visibility.Collapsed;
        UpdateSizeReadout();
        UpdateCursorPreview();
    }

    private void SetColor(Color color)
    {
        _color = color;
        ColorSwatch.Background = new SolidColorBrush(color);
        if (TextEditor.Visibility == Visibility.Visible)
            TextEditor.Foreground = new SolidColorBrush(color);
        UpdateCursorPreview();
    }

    private bool IsEffectTool => _tool is ToolKind.Mosaic or ToolKind.Blur;

    private bool IsStrokeTool => _tool is ToolKind.Pen or ToolKind.Highlighter or ToolKind.Line
        or ToolKind.Arrow or ToolKind.Rectangle or ToolKind.Ellipse;

    private void UpdateSizeReadout()
    {
        SizeReadout.Text = _tool switch
        {
            ToolKind.None => "휠: 굵기",
            ToolKind.Mosaic or ToolKind.Blur => $"세기 {_effectStrength}",
            ToolKind.Text => $"글자 {_fontSize:0}",
            _ => $"굵기 {_thickness:0}",
        };
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);

        // A modifier-drag shape is adjustable even with no toolbar tool selected.
        if (_tool == ToolKind.None && _current is null) return;

        int step = Math.Sign(e.Delta);

        if (_current is EffectAnnotation drawingEffect)
        {
            _effectStrength = Math.Clamp(_effectStrength + step * 2, 4, 60);
            drawingEffect.Strength = _effectStrength;
        }
        else if (_current is not null)
        {
            _thickness = DrawingShortcuts.AdjustThickness(_thickness, step);
            _current.Thickness = _thickness;
        }
        else if (IsEffectTool)
        {
            _effectStrength = Math.Clamp(_effectStrength + step * 2, 4, 60);
        }
        else if (_tool == ToolKind.Text)
        {
            _fontSize = Math.Clamp(_fontSize + step * 2, 10, 120);
            if (TextEditor.Visibility == Visibility.Visible) TextEditor.FontSize = _fontSize;
        }
        else
        {
            _thickness = DrawingShortcuts.AdjustThickness(_thickness, step);
        }

        UpdateSizeReadout();
        UpdateCursorPreview();
        Layer.InvalidateVisual();
        e.Handled = true;
    }

    private void UpdateCursorPreview()
    {
        bool show = IsStrokeTool && _hasSelection && _selection.Contains(_cursor);
        Layer.CursorPreviewAt = show ? _cursor : null;
        Layer.CursorPreviewColor = _color;
        Layer.CursorPreviewSize = _tool == ToolKind.Highlighter ? _thickness * 3 : _thickness;
        Layer.InvalidateVisual();
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsOverChrome(e)) return;

        ColorPalette.Visibility = Visibility.Collapsed;
        if (TextEditor.Visibility == Visibility.Visible)
        {
            CommitText();
            return;
        }

        Point p = e.GetPosition(Surface);
        _dragOrigin = p;
        _dragMoved = false;
        _restoreSelection = null;

        if (_hasSelection)
        {
            bool inside = _selection.Contains(p);
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            // Alt is the only way left to shove the selection around, now that a plain drag draws.
            if (alt && inside)
            {
                _drag = DragMode.Moving;
                _dragStartRect = _selection;
            }
            // A held shape modifier always means "draw this shape", identical to ScreenNote. It wins
            // over the toolbar tool and over the resize handles so the two programs never diverge.
            else if (HasShapeModifier() && inside)
            {
                BeginShapeDrag(p);
            }
            else if (HitHandle(p) is var handle && handle >= 0)
            {
                _drag = DragMode.Resizing;
                _activeHandle = handle;
                _dragStartRect = _selection;
            }
            else if (inside)
            {
                // Plain drag draws, same as ScreenNote. A toolbar tool just picks what it draws with.
                if (_tool != ToolKind.None) BeginAnnotation(p);
                else BeginShapeDrag(p);
            }
            else
            {
                _drag = DragMode.Creating;
                _restoreSelection = _selection;
                SetSelection(new Rect(p, p));
            }
        }
        else
        {
            _drag = DragMode.Creating;
            SetSelection(new Rect(p, p));
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        Point p = e.GetPosition(Surface);
        _cursor = p;

        if (_drag != DragMode.None && (p - _dragOrigin).Length > 2) _dragMoved = true;

        switch (_drag)
        {
            case DragMode.Creating:
                SetSelection(Normalize(_dragOrigin, p));
                break;

            case DragMode.Moving:
                var moved = _dragStartRect;
                moved.Offset(p.X - _dragOrigin.X, p.Y - _dragOrigin.Y);
                SetSelection(ClampToScreen(moved));
                break;

            case DragMode.Resizing:
                SetSelection(ClampToScreen(ResizeRect(_dragStartRect, _activeHandle, p)));
                break;

            case DragMode.Drawing:
                UpdateAnnotation(p);
                break;

            case DragMode.None:
                if (!_hasSelection) UpdateHoverWindow(p);
                else UpdateResizeCursor(p);
                break;
        }

        UpdateMagnifier(p);
        UpdateCursorPreview();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragMode.None) return;

        Point p = e.GetPosition(Surface);
        ReleaseMouseCapture();

        if (_drag == DragMode.Creating && !_dragMoved)
        {
            // A stray click next to an existing selection must not throw away the annotations on it.
            if (_restoreSelection is { } previous) SetSelection(previous);
            else if (_hoverWindow is { } window) SetSelection(ClampToScreen(window));
            else SetSelection(Rect.Empty);
        }
        else if (_drag == DragMode.Drawing)
        {
            CommitAnnotation();
        }

        _drag = DragMode.None;
        _activeHandle = -1;

        if (_selection.Width < MinSelection || _selection.Height < MinSelection)
        {
            SetSelection(Rect.Empty);
        }

        UpdateChrome();
        UpdateMagnifier(p);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        e.Handled = true;

        if (_drag == DragMode.Drawing)
        {
            _current = null;
            Layer.Preview = null;
            _drag = DragMode.None;
            ReleaseMouseCapture();
            Layer.InvalidateVisual();
        }
        else if (_tool != ToolKind.None)
        {
            SetTool(ToolKind.None);
        }
        else
        {
            Close();
        }
    }

    private bool IsOverChrome(MouseButtonEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node is not null && node is Visual)
        {
            if (ReferenceEquals(node, Toolbar) || ReferenceEquals(node, ColorPalette)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ------------------------------------------------------------ selection

    private static Rect Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private Rect ClampToScreen(Rect r)
    {
        double w = _shot.PixelWidth, h = _shot.PixelHeight;
        double x = Math.Clamp(r.X, 0, Math.Max(0, w - r.Width));
        double y = Math.Clamp(r.Y, 0, Math.Max(0, h - r.Height));
        double width = Math.Min(r.Width, w - x);
        double height = Math.Min(r.Height, h - y);
        return new Rect(x, y, Math.Max(0, width), Math.Max(0, height));
    }

    private static Rect ResizeRect(Rect r, int handle, Point p)
    {
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        switch (handle)
        {
            case 0: left = p.X; top = p.Y; break;
            case 1: top = p.Y; break;
            case 2: right = p.X; top = p.Y; break;
            case 3: right = p.X; break;
            case 4: right = p.X; bottom = p.Y; break;
            case 5: bottom = p.Y; break;
            case 6: left = p.X; bottom = p.Y; break;
            case 7: left = p.X; break;
        }
        return new Rect(Math.Min(left, right), Math.Min(top, bottom),
            Math.Abs(right - left), Math.Abs(bottom - top));
    }

    private void SetSelection(Rect rect)
    {
        _selection = rect;
        _hasSelection = rect.Width >= MinSelection && rect.Height >= MinSelection;

        Layer.Selection = _hasSelection ? _selection : Rect.Empty;

        var full = new RectangleGeometry(new Rect(0, 0, _shot.PixelWidth, _shot.PixelHeight));
        Mask.Data = _hasSelection
            ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_selection))
            : full;

        UpdateChrome();
    }

    private void UpdateChrome()
    {
        if (!_hasSelection)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Collapsed;
            ColorPalette.Visibility = Visibility.Collapsed;
            HintBar.Visibility = Visibility.Visible;
            foreach (var handle in _handles) handle.Visibility = Visibility.Collapsed;
            return;
        }

        // Once a selection exists the hint only stays up while F1 is holding it open.
        if (!_hintTimer.IsEnabled) HintBar.Visibility = Visibility.Collapsed;

        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Width = _selection.Width;
        SelectionBorder.Height = _selection.Height;
        Canvas.SetLeft(SelectionBorder, _selection.X);
        Canvas.SetTop(SelectionBorder, _selection.Y);

        PositionHandles();
        PositionSizeLabel();

        bool interacting = _drag is DragMode.Creating or DragMode.Resizing or DragMode.Moving or DragMode.Drawing;
        Toolbar.Visibility = interacting ? Visibility.Hidden : Visibility.Visible;
        if (!interacting) PositionToolbar();
    }

    private void PositionHandles()
    {
        double left = _selection.Left, top = _selection.Top;
        double right = _selection.Right, bottom = _selection.Bottom;
        double midX = left + _selection.Width / 2, midY = top + _selection.Height / 2;

        Point[] points =
        {
            new(left, top), new(midX, top), new(right, top), new(right, midY),
            new(right, bottom), new(midX, bottom), new(left, bottom), new(left, midY),
        };

        for (int i = 0; i < _handles.Length; i++)
        {
            _handles[i].Visibility = Visibility.Visible;
            Canvas.SetLeft(_handles[i], points[i].X - HandleSize / 2);
            Canvas.SetTop(_handles[i], points[i].Y - HandleSize / 2);
        }
    }

    private int HitHandle(Point p)
    {
        double left = _selection.Left, top = _selection.Top;
        double right = _selection.Right, bottom = _selection.Bottom;
        double midX = left + _selection.Width / 2, midY = top + _selection.Height / 2;

        Point[] points =
        {
            new(left, top), new(midX, top), new(right, top), new(right, midY),
            new(right, bottom), new(midX, bottom), new(left, bottom), new(left, midY),
        };

        for (int i = 0; i < points.Length; i++)
            if (Math.Abs(p.X - points[i].X) <= HandleHitRadius && Math.Abs(p.Y - points[i].Y) <= HandleHitRadius)
                return i;

        return -1;
    }

    private void UpdateResizeCursor(Point p)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && _selection.Contains(p))
        {
            Cursor = Cursors.SizeAll;
            return;
        }

        Cursor = HitHandle(p) switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            3 or 7 => Cursors.SizeWE,
            _ => Cursors.Cross,
        };
    }

    private void PositionSizeLabel()
    {
        SizeLabel.Visibility = Visibility.Visible;
        SizeText.Text = $"{_selection.Width:0} × {_selection.Height:0}";
        SizeLabel.UpdateLayout();

        double y = _selection.Top - SizeLabel.ActualHeight - 6;
        if (y < 2) y = _selection.Top + 6;
        double x = Math.Min(_selection.Left, _shot.PixelWidth - SizeLabel.ActualWidth - 2);

        Canvas.SetLeft(SizeLabel, Math.Max(2, x));
        Canvas.SetTop(SizeLabel, y);
    }

    private void PositionToolbar()
    {
        Toolbar.UpdateLayout();
        double width = Toolbar.ActualWidth, height = Toolbar.ActualHeight;

        double x = _selection.Right - width;
        x = Math.Clamp(x, 4, Math.Max(4, _shot.PixelWidth - width - 4));

        double y = _selection.Bottom + 8;
        if (y + height > _shot.PixelHeight - 4)
        {
            y = _selection.Top - height - 8;
            if (y < 4) y = Math.Max(4, _shot.PixelHeight - height - 4);
        }

        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
    }

    private void PositionPalette()
    {
        ColorPalette.UpdateLayout();
        double x = Canvas.GetLeft(Toolbar) + Math.Max(0, Toolbar.ActualWidth - ColorPalette.ActualWidth);
        double y = Canvas.GetTop(Toolbar) - ColorPalette.ActualHeight - 6;
        if (y < 4) y = Canvas.GetTop(Toolbar) + Toolbar.ActualHeight + 6;

        Canvas.SetLeft(ColorPalette, Math.Max(4, x));
        Canvas.SetTop(ColorPalette, y);
    }

    private void PositionHintBar()
    {
        HintBar.UpdateLayout();
        Canvas.SetLeft(HintBar, (_shot.PixelWidth - HintBar.ActualWidth) / 2);
        Canvas.SetTop(HintBar, 40);
    }

    private void ShowHint()
    {
        HintBar.Visibility = Visibility.Visible;
        PositionHintBar();
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void UpdateHoverWindow(Point p)
    {
        var rect = NativeMethods.GetWindowRectUnderPoint(
            _bounds.Left + (int)p.X, _bounds.Top + (int)p.Y,
            new WindowInteropHelper(this).Handle);

        if (rect is null)
        {
            _hoverWindow = null;
            HoverHint.Visibility = Visibility.Collapsed;
            return;
        }

        var value = rect.Value;
        var local = new Rect(value.Left - _bounds.Left, value.Top - _bounds.Top, value.Width, value.Height);
        local = ClampToScreen(local);
        _hoverWindow = local;

        HoverHint.Visibility = Visibility.Visible;
        HoverHint.Width = local.Width;
        HoverHint.Height = local.Height;
        Canvas.SetLeft(HoverHint, local.X);
        Canvas.SetTop(HoverHint, local.Y);
    }

    private void UpdateMagnifier(Point p)
    {
        if (_hasSelection || _drag is DragMode.Drawing or DragMode.Moving or DragMode.Resizing)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        const int span = 13;
        int x = Math.Clamp((int)p.X - span / 2, 0, Math.Max(0, _shot.PixelWidth - span));
        int y = Math.Clamp((int)p.Y - span / 2, 0, Math.Max(0, _shot.PixelHeight - span));

        MagImage.Source = new CroppedBitmap(_shot, new Int32Rect(x, y, span, span));

        var pixel = new byte[4];
        int px = Math.Clamp((int)p.X, 0, _shot.PixelWidth - 1);
        int py = Math.Clamp((int)p.Y, 0, _shot.PixelHeight - 1);
        _shot.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);

        MagInfo.Text = $"{_bounds.Left + px}, {_bounds.Top + py}\n#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";

        Magnifier.Visibility = Visibility.Visible;
        Magnifier.UpdateLayout();

        double mx = p.X + 22;
        double my = p.Y + 22;
        if (mx + Magnifier.ActualWidth > _shot.PixelWidth) mx = p.X - Magnifier.ActualWidth - 22;
        if (my + Magnifier.ActualHeight > _shot.PixelHeight) my = p.Y - Magnifier.ActualHeight - 22;

        Canvas.SetLeft(Magnifier, Math.Max(0, mx));
        Canvas.SetTop(Magnifier, Math.Max(0, my));
    }

    // ----------------------------------------------------------- annotation

    private static bool HasShapeModifier() =>
        (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 || Keyboard.IsKeyDown(Key.Tab);

    private bool IsHighlighting => _tool == ToolKind.Highlighter;

    /// <summary>Starts one of the shared modifier-drag shapes.</summary>
    private void BeginShapeDrag(Point p)
    {
        var kind = DrawingShortcuts.ShapeFromKeyboard();
        _current = DrawingShortcuts.Create(kind, p, _color, _thickness, IsHighlighting);

        Layer.Preview = _current;
        _drag = DragMode.Drawing;
        SizeReadout.Text = DrawingShortcuts.ShapeLabel(kind);
        UpdateChrome();
        Layer.InvalidateVisual();
    }

    private void BeginAnnotation(Point p)
    {
        if (_tool == ToolKind.Text)
        {
            StartTextEditing(p);
            return;
        }

        // Stamps land on a single click; nothing is dragged out.
        if (DrawingShortcuts.IsStamp(_tool))
        {
            Layer.Items.Add(DrawingShortcuts.StampFor(_tool) is { } stamp
                ? new StampAnnotation { Center = p, Kind = stamp, Color = _color, Thickness = _thickness }
                : new NumberAnnotation
                {
                    Center = p,
                    Value = Layer.Items.OfType<NumberAnnotation>().Count() + 1,
                    Color = _color,
                    Thickness = _thickness,
                });

            _redo.Clear();
            UpdateUndoRedoState();
            Layer.InvalidateVisual();
            return;
        }

        _current = _tool switch
        {
            ToolKind.Mosaic => new EffectAnnotation { Start = p, End = p, Kind = EffectKind.Mosaic, Strength = _effectStrength },
            ToolKind.Blur => new EffectAnnotation { Start = p, End = p, Kind = EffectKind.Blur, Strength = _effectStrength },

            ToolKind.Line => DrawingShortcuts.Create(ShapeKind.Line, p, _color, _thickness),
            ToolKind.Arrow => DrawingShortcuts.Create(ShapeKind.Arrow, p, _color, _thickness),
            ToolKind.Rectangle => DrawingShortcuts.Create(ShapeKind.Rectangle, p, _color, _thickness),
            ToolKind.Ellipse => DrawingShortcuts.Create(ShapeKind.Ellipse, p, _color, _thickness),

            // Pen and highlighter both honour the drag modifiers, so Shift gives a straight highlight.
            _ => DrawingShortcuts.Create(DrawingShortcuts.ShapeFromKeyboard(), p, _color, _thickness, IsHighlighting),
        };

        if (_current is null) return;

        _current.Color = _color;
        _current.Thickness = _thickness;

        Layer.Preview = _current;
        _drag = DragMode.Drawing;
        UpdateChrome();
        Layer.InvalidateVisual();
    }

    private void UpdateAnnotation(Point p)
    {
        if (_current is null) return;

        p = new Point(
            Math.Clamp(p.X, _selection.Left, _selection.Right),
            Math.Clamp(p.Y, _selection.Top, _selection.Bottom));

        DrawingShortcuts.Extend(_current, p);
        Layer.InvalidateVisual();
    }

    private void CommitAnnotation()
    {
        Layer.Preview = null;

        if (_current is { IsValid: true })
        {
            Layer.Items.Add(_current);
            _redo.Clear();
        }

        _current = null;
        UpdateUndoRedoState();
        Layer.InvalidateVisual();
    }

    private void StartTextEditing(Point p)
    {
        _textOrigin = p;
        TextEditor.Text = string.Empty;
        TextEditor.FontSize = _fontSize;
        TextEditor.Foreground = new SolidColorBrush(_color);
        TextEditor.MaxWidth = Math.Max(60, _selection.Right - p.X);
        TextEditor.Visibility = Visibility.Visible;

        Canvas.SetLeft(TextEditor, p.X);
        Canvas.SetTop(TextEditor, p.Y);

        TextEditor.UpdateLayout();
        TextEditor.Focus();
        Keyboard.Focus(TextEditor);
    }

    private void TextEditorOnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelText();
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            CommitText();
        }
    }

    private void CancelText()
    {
        TextEditor.Text = string.Empty;
        TextEditor.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void CommitText()
    {
        if (TextEditor.Visibility != Visibility.Visible) return;

        string value = TextEditor.Text;
        TextEditor.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(value))
        {
            Layer.Items.Add(new TextAnnotation
            {
                // Compensate for the editor's border and padding so the glyphs land where they were typed.
                Origin = new Point(_textOrigin.X + 3, _textOrigin.Y + 1),
                Text = value,
                FontSize = _fontSize,
                Color = _color,
            });
            _redo.Clear();
            UpdateUndoRedoState();
            Layer.InvalidateVisual();
        }

        TextEditor.Text = string.Empty;
        Focus();
    }

    private void Undo()
    {
        CommitText();
        if (Layer.Items.Count == 0) return;

        var last = Layer.Items[^1];
        Layer.Items.RemoveAt(Layer.Items.Count - 1);
        _redo.Push(last);

        UpdateUndoRedoState();
        Layer.InvalidateVisual();
    }

    private void EraseAll()
    {
        CommitText();
        if (Layer.Items.Count == 0) return;

        for (int i = Layer.Items.Count - 1; i >= 0; i--) _redo.Push(Layer.Items[i]);
        Layer.Items.Clear();

        UpdateUndoRedoState();
        Layer.InvalidateVisual();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;

        Layer.Items.Add(_redo.Pop());
        UpdateUndoRedoState();
        Layer.InvalidateVisual();
    }

    private void UpdateUndoRedoState()
    {
        BtnUndo.IsEnabled = Layer.Items.Count > 0;
        BtnRedo.IsEnabled = _redo.Count > 0;
    }

    // --------------------------------------------------------------- output

    private Int32Rect SnappedSelection()
    {
        int x = (int)Math.Round(_selection.X);
        int y = (int)Math.Round(_selection.Y);
        int w = Math.Max(1, (int)Math.Round(_selection.Width));
        int h = Math.Max(1, (int)Math.Round(_selection.Height));

        w = Math.Min(w, _shot.PixelWidth - x);
        h = Math.Min(h, _shot.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    private BitmapSource? Compose()
    {
        CommitText();
        if (!_hasSelection) return null;

        var region = SnappedSelection();
        var area = new Rect(region.X, region.Y, region.Width, region.Height);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-area.X, -area.Y));
            dc.DrawImage(_shot, new Rect(0, 0, _shot.PixelWidth, _shot.PixelHeight));
            dc.PushClip(new RectangleGeometry(area));
            foreach (var item in Layer.Items) item.Render(dc, _context);
            dc.Pop();
            dc.Pop();
        }

        var target = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private void FinishCopy()
    {
        var image = Compose();
        if (image is null) return;

        Close();
        TrySetClipboard(image);
    }

    /// <summary>
    /// Straight to the configured folder with no dialog — during a lesson the prompt is the slow part.
    /// <paramref name="chooseLocation"/> brings the dialog back for the occasional one-off.
    /// </summary>
    private void FinishSave(bool chooseLocation = false)
    {
        var image = Compose();
        if (image is null) return;

        var region = SnappedSelection();
        Close();

        string folder = AppSettings.EnsureSaveFolder();
        string name = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}_{region.Width}x{region.Height}.png";
        string path = Path.Combine(folder, name);

        if (chooseLocation)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PNG 이미지 (*.png)|*.png",
                InitialDirectory = folder,
                FileName = name,
                AddExtension = true,
            };

            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Notify("저장하지 못했습니다", ex.Message, warning: true);
            return;
        }

        // No dialog means no other sign it worked, so say where it landed.
        App.Notify("저장했습니다", path);
    }

    private void FinishPin()
    {
        var image = Compose();
        if (image is null) return;

        var region = SnappedSelection();
        var screenPoint = new Point(_bounds.Left + region.X, _bounds.Top + region.Y);

        Close();
        PinWindow.Pin(image, screenPoint);
    }

    private static void TrySetClipboard(BitmapSource image)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }
    }

    private void CopyHexUnderCursor()
    {
        var pixel = new byte[4];
        int px = Math.Clamp((int)_cursor.X, 0, _shot.PixelWidth - 1);
        int py = Math.Clamp((int)_cursor.Y, 0, _shot.PixelHeight - 1);
        _shot.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);

        try
        {
            Clipboard.SetText($"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}",
                TextDataFormat.UnicodeText);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process owns the clipboard; nothing useful to do here.
        }
    }

    // ------------------------------------------------------------- keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (TextEditor.Visibility == Visibility.Visible) return;

        // Tab is the ellipse drag modifier here, never focus navigation.
        if (e.Key == Key.Tab || (e.Key == Key.System && e.SystemKey == Key.Tab))
        {
            e.Handled = true;
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when shift:
                case Key.Y: Redo(); e.Handled = true; return;
                case Key.Z: Undo(); e.Handled = true; return;
                case Key.C: FinishCopy(); e.Handled = true; return;
                case Key.S: FinishSave(); e.Handled = true; return;
                case Key.D: FinishPin(); e.Handled = true; return;
                // Ctrl+Alt+S opens the dialog. Ctrl+Shift+S is off limits - it is the global capture key.
                case Key.System when e.SystemKey == Key.S && alt:
                    FinishSave(chooseLocation: true);
                    e.Handled = true;
                    return;
                case Key.A:
                    SetSelection(new Rect(0, 0, _shot.PixelWidth, _shot.PixelHeight));
                    e.Handled = true;
                    return;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (_tool != ToolKind.None) SetTool(ToolKind.None);
                else if (_hasSelection) SetSelection(Rect.Empty);
                else Close();
                e.Handled = true;
                return;

            case Key.Enter:
                FinishCopy();
                e.Handled = true;
                return;

            case Key.E: EraseAll(); break;
            // F1 stays usable here because the global capture key is Ctrl+F1, not bare F1.
            case Key.F1: ShowHint(); break;
            case Key.C when !_hasSelection: CopyHexUnderCursor(); break;

            default:
                // Colours and tool letters come from the shared table, never from a copy kept here.
                if (DrawingShortcuts.ColorFor(e.Key) is { } color)
                {
                    SetColor(color);
                    break;
                }
                if (DrawingShortcuts.ToolFor(e.Key, shift) is { } tool)
                {
                    SetTool(tool);
                    break;
                }
                return;
        }

        e.Handled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        _closing = true;
        _hintTimer.Stop();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_activated || _closing) return;

        // Something stole focus. Reclaim it rather than discard work, but bail out if nothing is at stake.
        if (_hasSelection || Layer.Items.Count > 0) Activate();
        else Close();
    }
}
