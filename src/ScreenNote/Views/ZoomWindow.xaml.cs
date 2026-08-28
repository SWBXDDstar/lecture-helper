using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LectureHelper.Annotations;
using LectureHelper.Native;
using LectureHelper.Services;

namespace ScreenNote.Views;

/// <summary>
/// Ctrl+1. Freezes the screen and magnifies it around the cursor. Clicking anchors the view and
/// starts drawing with exactly the bindings the annotation overlay uses.
/// </summary>
public partial class ZoomWindow : Window
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 8.0;

    private readonly BitmapSource _shot;
    private readonly NativeMethods.RECT _bounds;
    private readonly RenderContext _context;
    private readonly DrawingSession _session;
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private readonly ScaleTransform _scale = new(2, 2);
    private readonly TranslateTransform _pan = new();

    private double _zoom = 2.0;
    private Point _offset;
    private bool _anchored;

    private Point _textOrigin;
    private bool _drawing;
    private bool _closing;
    private bool _activated;

    public ZoomWindow()
    {
        InitializeComponent();

        // 설정된 모니터 한 대만 덮는다. 전체를 얼리면 옆 화면의 채팅·자료를 못 본다.
        _bounds = OverlayTarget.Resolve(AppSettings.OverlayMonitor);
        _shot = ScreenGrabber.Capture(_bounds);
        _context = new RenderContext { Effects = new EffectCache(_shot) };
        _session = new DrawingSession(Layer.Items);

        Shot.Source = _shot;
        Shot.Width = _shot.PixelWidth;
        Shot.Height = _shot.PixelHeight;

        Viewport.Width = _shot.PixelWidth;
        Viewport.Height = _shot.PixelHeight;

        Stage.Width = _shot.PixelWidth;
        Stage.Height = _shot.PixelHeight;
        Stage.RenderTransform = new TransformGroup { Children = { _scale, _pan } };

        Layer.Width = _shot.PixelWidth;
        Layer.Height = _shot.PixelHeight;
        Layer.Context = _context;
        Layer.Selection = new Rect(0, 0, _shot.PixelWidth, _shot.PixelHeight);

        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            StatusBar.Visibility = Visibility.Collapsed;
            HintBar.Visibility = Visibility.Collapsed;
        };

        TextEditor.PreviewKeyDown += TextEditorOnPreviewKeyDown;
        TextEditor.LostKeyboardFocus += (_, _) => CommitText();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(handle, IntPtr.Zero,
            _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);

        var dpi = VisualTreeHelper.GetDpi(this);
        Root.LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);

        NativeMethods.SetForegroundWindow(handle);
        Activate();
        Focus();

        NativeMethods.GetCursorPos(out var cursor);
        FollowCursor(new Point(cursor.X - _bounds.Left, cursor.Y - _bounds.Top));
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

    // ----------------------------------------------------------------- view

    /// <summary>
    /// Maps the cursor across the full screen onto the full pannable range, so every edge of the
    /// desktop stays reachable no matter how far in we are.
    /// </summary>
    private void FollowCursor(Point cursor)
    {
        double maxX = Math.Max(0, _shot.PixelWidth - _shot.PixelWidth / _zoom);
        double maxY = Math.Max(0, _shot.PixelHeight - _shot.PixelHeight / _zoom);

        _offset = new Point(
            Math.Clamp(cursor.X / _shot.PixelWidth, 0, 1) * maxX,
            Math.Clamp(cursor.Y / _shot.PixelHeight, 0, 1) * maxY);

        ApplyView();
        SyncHintIfVisible();
    }

    private void ApplyView()
    {
        double maxX = Math.Max(0, _shot.PixelWidth - _shot.PixelWidth / _zoom);
        double maxY = Math.Max(0, _shot.PixelHeight - _shot.PixelHeight / _zoom);
        _offset = new Point(Math.Clamp(_offset.X, 0, maxX), Math.Clamp(_offset.Y, 0, maxY));

        _scale.ScaleX = _scale.ScaleY = _zoom;
        _pan.X = -_offset.X * _zoom;
        _pan.Y = -_offset.Y * _zoom;
    }

    /// <summary>Viewport pixels to screenshot pixels.</summary>
    private Point ToContent(Point viewPoint) => new(
        _offset.X + viewPoint.X / _zoom,
        _offset.Y + viewPoint.Y / _zoom);

    private void SetZoom(double zoom, Point viewAnchor)
    {
        double previous = _zoom;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(previous - _zoom) < 0.0001) return;

        // Keep whatever sits under the cursor pinned there while the magnification changes.
        Point content = new(_offset.X + viewAnchor.X / previous, _offset.Y + viewAnchor.Y / previous);
        _offset = new Point(content.X - viewAnchor.X / _zoom, content.Y - viewAnchor.Y / _zoom);

        ApplyView();
    }

    private void RefreshStatus()
    {
        HintSwatch.Background = new SolidColorBrush(_session.Color);
        HintStatus.Text = $"{_zoom * 100:0}%  ·  {_session.StatusText}";
    }

    /// <summary>
    /// 안내가 떠 있는 동안 배율·색·두께가 바뀌면 숫자도 따라가게 한다.
    /// 안내가 숨어 있으면 아무 일도 안 한다 — 저절로 뜨는 일은 없다.
    /// </summary>
    private void SyncHintIfVisible()
    {
        if (StatusBar.Visibility != Visibility.Visible) return;

        RefreshStatus();
        PositionHintBar();
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void Anchor()
    {
        _anchored = true;
        SyncCursor();
    }

    private void SyncCursor() =>
        Cursor = _session.IsTextMode ? Cursors.IBeam : (_anchored ? Cursors.Pen : Cursors.Cross);

    // ----------------------------------------------------------------- 안내

    /// <summary>상태는 위, 안내는 아래. 옛 HUD가 있던 자리를 상태줄이 그대로 물려받았다.</summary>
    private void PositionHintBar()
    {
        StatusBar.UpdateLayout();
        Canvas.SetLeft(StatusBar, (_shot.PixelWidth - StatusBar.ActualWidth) / 2);
        Canvas.SetTop(StatusBar, 28);

        HintBar.UpdateLayout();
        Canvas.SetLeft(HintBar, (_shot.PixelWidth - HintBar.ActualWidth) / 2);
        Canvas.SetTop(HintBar, _shot.PixelHeight - HintBar.ActualHeight - 40);
    }

    /// <summary>
    /// F1 로 불렀을 때만 뜬다. 발표 화면을 덮지 않으려고 저절로는 띄우지 않는다.
    /// 배율과 색·두께도 같이 보여준다 — 저절로 뜨던 알약을 없앤 대신 여기서 한 번에 확인한다.
    /// </summary>
    private void ShowHint()
    {
        RefreshStatus();

        StatusBar.Visibility = Visibility.Visible;
        HintBar.Visibility = Visibility.Visible;
        PositionHintBar();
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point view = e.GetPosition(Viewport);

        if (_drawing)
        {
            _session.Extend(ToContent(view));
            Layer.InvalidateVisual();
            return;
        }

        // Panning stops once the view is anchored, so a drawing hand does not drag the picture around.
        if (!_anchored) FollowCursor(view);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (TextEditor.Visibility == Visibility.Visible)
        {
            CommitText();
            return;
        }

        Point view = e.GetPosition(Viewport);
        Anchor();

        if (_session.IsTextMode)
        {
            StartTextEditing(view);
            e.Handled = true;
            return;
        }

        if (_session.Begin(ToContent(view)))
        {
            _drawing = true;
            Layer.Preview = _session.Current;
            CaptureMouse();
        }

        Layer.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_drawing) return;

        _drawing = false;
        ReleaseMouseCapture();
        Layer.Preview = null;
        _session.Commit();

        Layer.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        e.Handled = true;

        if (_drawing)
        {
            _drawing = false;
            _session.Cancel();
            Layer.Preview = null;
            ReleaseMouseCapture();
            Layer.InvalidateVisual();
            return;
        }

        if (_session.Tool != ToolKind.None)
        {
            CommitText();
            _session.SetTool(ToolKind.None);
            SyncCursor();
            SyncHintIfVisible();
            return;
        }

        Close();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        int step = Math.Sign(e.Delta);

        // Drawing, or holding Ctrl, means the wheel is a tool control. Otherwise it is the zoom.
        bool toolWheel = _drawing
            || _session.IsTextMode
            || (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (toolWheel)
        {
            _session.Adjust(step);
            if (TextEditor.Visibility == Visibility.Visible) TextEditor.FontSize = _session.FontSize * _zoom;
            SyncHintIfVisible();
            Layer.InvalidateVisual();
        }
        else
        {
            SetZoom(_zoom * (step > 0 ? 1.2 : 1 / 1.2), e.GetPosition(Viewport));
        }

        e.Handled = true;
    }

    // ----------------------------------------------------------------- text

    /// <summary>The editor sits in viewport space, so its font is scaled up to match the zoom.</summary>
    private void StartTextEditing(Point view)
    {
        _textOrigin = ToContent(view);

        TextEditor.Text = string.Empty;
        TextEditor.FontSize = _session.FontSize * _zoom;
        TextEditor.Foreground = new SolidColorBrush(_session.Color);
        TextEditor.MaxWidth = Math.Max(80, _shot.PixelWidth - view.X - 20);
        TextEditor.Visibility = Visibility.Visible;

        Canvas.SetLeft(TextEditor, view.X);
        Canvas.SetTop(TextEditor, view.Y);

        TextEditor.UpdateLayout();
        Keyboard.Focus(TextEditor);
    }

    private void TextEditorOnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            TextEditor.Text = string.Empty;
            TextEditor.Visibility = Visibility.Collapsed;
            Focus();
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            CommitText();
        }
    }

    private void CommitText()
    {
        if (TextEditor.Visibility != Visibility.Visible) return;

        string value = TextEditor.Text;
        TextEditor.Visibility = Visibility.Collapsed;

        // Back to content space, compensating for the editor's border and padding.
        _session.AddText(new Point(_textOrigin.X + 3 / _zoom, _textOrigin.Y + 1 / _zoom), value);

        TextEditor.Text = string.Empty;
        Layer.InvalidateVisual();
        Focus();
    }

    // ------------------------------------------------------------- keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (TextEditor.Visibility == Visibility.Visible) return;

        if (e.Key == Key.Tab || (e.Key == Key.System && e.SystemKey == Key.Tab))
        {
            e.Handled = true;
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var center = new Point(_shot.PixelWidth / 2.0, _shot.PixelHeight / 2.0);

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when shift:
                case Key.Y: _session.Redo(); Layer.InvalidateVisual(); e.Handled = true; return;
                case Key.Z: _session.Undo(); Layer.InvalidateVisual(); e.Handled = true; return;
            }
            return;
        }

        switch (e.Key)
        {
            // X도 나가기다. 캔바 발표 중에 헛발질로 새어나가도 캔바가 무시하는 키라
            // Esc처럼 발표 모드를 끝내버리지 않는다 (26.08.23).
            case Key.Escape:
            case Key.X:
                if (_session.Tool != ToolKind.None)
                {
                    CommitText();
                    _session.SetTool(ToolKind.None);
                    SyncCursor();
                }
                else Close();
                break;

            case Key.E:
                _session.EraseAll();
                Layer.InvalidateVisual();
                break;

            case Key.F1: ShowHint(); break;

            case Key.Up: SetZoom(_zoom * 1.2, center); break;
            case Key.Down: SetZoom(_zoom / 1.2, center); break;

            case Key.Space:
                // Release the anchor so the view follows the cursor again. The tool is left alone.
                CommitText();
                _anchored = false;
                SyncCursor();
                break;

            default:
                if (!_session.HandleKey(e.Key, shift, out _)) return;
                CommitText();
                Anchor();
                SyncCursor();
                SyncHintIfVisible();
                Layer.InvalidateVisual();
                break;
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

        if (Layer.Items.Count > 0) Activate();
        else Close();
    }
}
