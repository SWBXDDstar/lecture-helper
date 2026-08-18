using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using LectureHelper.Native;
using LectureHelper.Services;

namespace ScreenCapture.Views;

/// <summary>A pinned screenshot that floats above every other window, Snipaste-style.</summary>
public partial class PinWindow : Window
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;

    private readonly BitmapSource _image;
    private readonly DispatcherTimer _badgeTimer;

    private double _zoom = 1.0;
    private double _opacity = 1.0;
    private int _physicalLeft;
    private int _physicalTop;
    private double _dpiScale = 1.0;

    private bool _dragging;
    private NativeMethods.POINT _dragAnchor;
    private int _dragStartLeft;
    private int _dragStartTop;

    public static void Pin(BitmapSource image, Point? screenTopLeft)
    {
        if (screenTopLeft is null)
        {
            NativeMethods.GetCursorPos(out var cursor);
            screenTopLeft = new Point(cursor.X - image.PixelWidth / 2.0, cursor.Y - image.PixelHeight / 2.0);
        }

        new PinWindow(image, screenTopLeft.Value).Show();
    }

    private PinWindow(BitmapSource image, Point screenTopLeft)
    {
        InitializeComponent();

        _image = image;
        PinImage.Source = image;

        _physicalLeft = (int)Math.Round(screenTopLeft.X) - 1;
        _physicalTop = (int)Math.Round(screenTopLeft.Y) - 1;

        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _badgeTimer.Tick += (_, _) => { _badgeTimer.Stop(); Badge.Visibility = Visibility.Collapsed; };

        MenuCopy.Click += (_, _) => CopyToClipboard();
        MenuSave.Click += (_, _) => SaveAs();
        MenuActualSize.Click += (_, _) => SetZoom(1.0);
        MenuOpaque.Click += (_, _) => SetOpacity(1.0);
        MenuClose.Click += (_, _) => Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ApplyGeometry();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX;
        ApplyGeometry();
    }

    /// <summary>Sizes and places the window in physical pixels so the pin matches the captured pixels 1:1.</summary>
    private void ApplyGeometry()
    {
        int width = (int)Math.Round(_image.PixelWidth * _zoom) + 2;
        int height = (int)Math.Round(_image.PixelHeight * _zoom) + 2;

        Root.LayoutTransform = new ScaleTransform(1 / _dpiScale, 1 / _dpiScale);
        Root.Width = width;
        Root.Height = height;

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, _physicalLeft, _physicalTop, width, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void SetZoom(double zoom)
    {
        double previous = _zoom;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(previous - _zoom) < 0.0001) return;

        // Grow and shrink around the window centre so the pin does not walk across the screen.
        int oldWidth = (int)Math.Round(_image.PixelWidth * previous);
        int oldHeight = (int)Math.Round(_image.PixelHeight * previous);
        int newWidth = (int)Math.Round(_image.PixelWidth * _zoom);
        int newHeight = (int)Math.Round(_image.PixelHeight * _zoom);

        _physicalLeft += (oldWidth - newWidth) / 2;
        _physicalTop += (oldHeight - newHeight) / 2;

        ApplyGeometry();
        ShowBadge($"{_zoom * 100:0}%");
    }

    private void SetOpacity(double value)
    {
        _opacity = Math.Clamp(value, 0.15, 1.0);
        Opacity = _opacity;
        ShowBadge($"불투명도 {_opacity * 100:0}%");
    }

    private void ShowBadge(string text)
    {
        BadgeText.Text = text;
        Badge.Visibility = Visibility.Visible;
        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int step = Math.Sign(e.Delta);

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            SetOpacity(_opacity + step * 0.08);
        else
            SetZoom(_zoom * (step > 0 ? 1.1 : 1 / 1.1));

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        NativeMethods.GetCursorPos(out _dragAnchor);
        _dragStartLeft = _physicalLeft;
        _dragStartTop = _physicalTop;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        NativeMethods.GetCursorPos(out var cursor);
        _physicalLeft = _dragStartLeft + (cursor.X - _dragAnchor.X);
        _physicalTop = _dragStartTop + (cursor.Y - _dragAnchor.Y);
        ApplyGeometry();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        int nudge = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;

        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.C when ctrl: CopyToClipboard(); break;
            case Key.S when ctrl: SaveAs(); break;
            case Key.D0 when ctrl: SetZoom(1.0); break;
            case Key.OemPlus when ctrl: SetZoom(_zoom * 1.1); break;
            case Key.OemMinus when ctrl: SetZoom(_zoom / 1.1); break;
            case Key.Left: _physicalLeft -= nudge; ApplyGeometry(); break;
            case Key.Right: _physicalLeft += nudge; ApplyGeometry(); break;
            case Key.Up: _physicalTop -= nudge; ApplyGeometry(); break;
            case Key.Down: _physicalTop += nudge; ApplyGeometry(); break;
            default: return;
        }

        e.Handled = true;
    }

    private void CopyToClipboard()
    {
        try
        {
            Clipboard.SetImage(_image);
            ShowBadge("복사됨");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ShowBadge("복사 실패");
        }
    }

    private void SaveAs()
    {
        // The pinned window is a deliberate act, so this one keeps the dialog.
        string folder = AppSettings.EnsureSaveFolder();

        bool wasTopmost = Topmost;
        Topmost = false;

        var dialog = new SaveFileDialog
        {
            Filter = "PNG 이미지 (*.png)|*.png",
            InitialDirectory = folder,
            FileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            AddExtension = true,
        };

        bool saved = dialog.ShowDialog() == true;
        Topmost = wasTopmost;
        if (!saved) return;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_image));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
        ShowBadge("저장됨");
    }
}
