using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LectureHelper.Native;
using LectureHelper.Services;
using Forms = System.Windows.Forms;

namespace LectureHelper.Views;

/// <summary>
/// 모니터 한가운데에 번호를 크게 띄운다. 윈도우 디스플레이 설정의 "식별" 버튼과 같은 역할.
///
/// 모니터가 넷쯤 되면 해상도가 다 같아서 "3번 모니터"라는 글자만으로는 어느 화면인지 알 수 없다.
/// 트레이 메뉴에 마우스를 올릴 때 실제 그 화면에 숫자를 띄워주면 바로 안다.
/// </summary>
public sealed class MonitorBadge : Window
{
    private static MonitorBadge? _current;

    private readonly TextBlock _label;

    private MonitorBadge()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        // 메뉴에서 포커스를 뺏어오면 메뉴가 닫혀버린다. 띄우되 활성화하지 않는다.
        ShowActivated = false;

        _label = new TextBlock
        {
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x7F, 0xE0)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(4),
            CornerRadius = new CornerRadius(18),
            Child = _label,
        };
    }

    /// <summary>해당 모니터 한가운데에 번호를 띄운다. 이미 떠 있으면 그 창을 옮겨 쓴다.</summary>
    public static void ShowOn(Forms::Screen screen)
    {
        _current ??= new MonitorBadge();
        _current.Place(screen);
    }

    public static void HideBadge()
    {
        _current?.Hide();
    }

    /// <summary>프로그램을 닫을 때 정리한다.</summary>
    public static void Dispose()
    {
        _current?.Close();
        _current = null;
    }

    private void Place(Forms::Screen screen)
    {
        var b = screen.Bounds;

        // 화면을 다 덮으면 위압적이다. 짧은 변의 3분의 1 정도면 멀리서도 보인다.
        int side = Math.Max(160, Math.Min(b.Width, b.Height) / 3);
        int left = b.Left + (b.Width - side) / 2;
        int top = b.Top + (b.Height - side) / 2;

        _label.Text = OverlayTarget.DisplayNumber(screen).ToString();
        _label.FontSize = side * 0.5;

        // 창이 만들어지기 전에는 핸들이 없다. 한 번 띄우고 자리를 잡는다.
        if (!IsVisible) Show();

        IntPtr handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, top, side, side,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        // 오버레이들과 같은 규약 — 물리 픽셀로 붙이고 루트에서 DPI를 되돌린다.
        var dpi = VisualTreeHelper.GetDpi(this);
        if (Content is FrameworkElement root)
            root.LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
    }
}
