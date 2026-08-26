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
/// Ctrl+2. Freezes the screen and turns the whole thing into a canvas — no region to pick, no file
/// to produce. Everything here is thrown away when the overlay closes.
/// </summary>
public partial class AnnotateWindow : Window
{
    private readonly BitmapSource _shot;
    private readonly NativeMethods.RECT _bounds;
    private readonly RenderContext _context;
    private readonly DrawingSession _session;
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private Point _textOrigin;
    private bool _drawing;
    private bool _closing;
    private bool _activated;

    public AnnotateWindow()
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

        Surface.Width = _shot.PixelWidth;
        Surface.Height = _shot.PixelHeight;

        Layer.Width = _shot.PixelWidth;
        Layer.Height = _shot.PixelHeight;
        Layer.Context = _context;
        Layer.Selection = new Rect(0, 0, _shot.PixelWidth, _shot.PixelHeight);

        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); HintBar.Visibility = Visibility.Collapsed; };

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

        // Work in physical pixels so mixed-DPI monitors line up 1:1 with the frozen screenshot.
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

    // ----------------------------------------------------------------- 안내

    private void PositionHintBar()
    {
        HintBar.UpdateLayout();
        Canvas.SetLeft(HintBar, (_shot.PixelWidth - HintBar.ActualWidth) / 2);
        Canvas.SetTop(HintBar, _shot.PixelHeight - HintBar.ActualHeight - 40);
    }

    /// <summary>
    /// F1 로 불렀을 때만 뜬다. 발표 화면을 덮지 않으려고 저절로는 띄우지 않는다.
    /// 지금 색·두께도 같이 보여준다 — 저절로 뜨던 알약을 없앤 대신 여기서 한 번에 확인한다.
    /// </summary>
    private void ShowHint()
    {
        HintSwatch.Background = new SolidColorBrush(_session.Color);
        HintStatus.Text = _session.StatusText;

        HintBar.Visibility = Visibility.Visible;
        PositionHintBar();
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void SyncCursor() => Cursor = _session.IsTextMode ? Cursors.IBeam : Cursors.Cross;

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (TextEditor.Visibility == Visibility.Visible)
        {
            CommitText();
            return;
        }

        Point p = e.GetPosition(Surface);

        if (_session.IsTextMode)
        {
            StartTextEditing(p);
            e.Handled = true;
            return;
        }

        if (_session.Begin(p))
        {
            _drawing = true;
            Layer.Preview = _session.Current;
            CaptureMouse();
        }

        Layer.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drawing) return;

        _session.Extend(e.GetPosition(Surface));
        Layer.InvalidateVisual();
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
            return;
        }

        Close();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);

        _session.Adjust(Math.Sign(e.Delta));
        if (TextEditor.Visibility == Visibility.Visible) TextEditor.FontSize = _session.FontSize;

        Layer.InvalidateVisual();
        e.Handled = true;
    }

    // ----------------------------------------------------------------- text

    private void StartTextEditing(Point p)
    {
        _textOrigin = p;
        TextEditor.Text = string.Empty;
        TextEditor.FontSize = _session.FontSize;
        TextEditor.Foreground = new SolidColorBrush(_session.Color);
        TextEditor.MaxWidth = Math.Max(80, _shot.PixelWidth - p.X - 20);
        TextEditor.Visibility = Visibility.Visible;

        Canvas.SetLeft(TextEditor, p.X);
        Canvas.SetTop(TextEditor, p.Y);

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

        // Compensate for the editor's border and padding so the glyphs land where they were typed.
        _session.AddText(new Point(_textOrigin.X + 3, _textOrigin.Y + 1), value);

        TextEditor.Text = string.Empty;
        Layer.InvalidateVisual();
        Focus();
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

            default:
                if (!_session.HandleKey(e.Key, shift, out _)) return;
                CommitText();
                SyncCursor();
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

        // Something stole focus mid-lesson. Reclaim it rather than wipe what is on the board.
        if (Layer.Items.Count > 0) Activate();
        else Close();
    }
}
