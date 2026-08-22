using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LectureHelper.Native;
using LectureHelper.Services;
using Forms = System.Windows.Forms;

namespace LectureHelper.Views;

/// <summary>
/// 모니터 한가운데에 번호를 크게 띄운다. 윈도우 디스플레이 설정의 "식별" 버튼과 같은 역할.
///
/// 모니터가 넷쯤 되면 해상도가 다 같아서 "3번 모니터"라는 글자만으로는 어느 화면인지 알 수 없다.
/// 고르고 나면 그 화면에 숫자가 잠깐 떠서 맞게 골랐는지 확인된다.
///
/// **메뉴가 닫힌 뒤에만 띄운다.** 처음엔 메뉴 항목에 마우스를 올릴 때 띄웠는데, 쿼드 모니터에서
/// 항목 사이를 오르내리면 메뉴가 닫히고 트레이 아이콘까지 사라졌다. WS_EX_NOACTIVATE 를 걸고
/// 창을 미리 만들어 둬도 해결되지 않아, 메뉴와 겹치는 시간을 아예 없앴다 (26.08.22).
/// </summary>
public sealed class MonitorBadge : Window
{
    private static MonitorBadge? _current;

    private readonly TextBlock _label;
    private DispatcherTimer? _timer;
    private bool _stylesApplied;

    private MonitorBadge()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
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

    /// <summary>
    /// 미리 만들어 둔다. 메뉴가 열린 상태에서 창을 처음 만들면 그 사이에 메시지 펌프가 돌아
    /// 메뉴가 닫힐 수 있다. 프로그램 시작할 때 한 번 불러 두면 그 위험이 사라진다.
    /// </summary>
    public static void Prepare()
    {
        try
        {
            _current ??= new MonitorBadge();
            _current.EnsureHandle();
        }
        catch (Exception)
        {
            // 뱃지는 편의 기능이다. 못 만들어도 프로그램은 계속 돌아야 한다.
        }
    }

    /// <summary>해당 모니터에 번호를 잠깐 띄웠다가 알아서 사라진다.</summary>
    public static void Flash(Forms::Screen screen, double seconds = 1.6)
    {
        try
        {
            _current ??= new MonitorBadge();
            _current.Place(screen);
            _current.StartFadeTimer(seconds);
        }
        catch (Exception)
        {
            // 트레이 메뉴 이벤트에서 던진 예외는 WPF가 못 잡고 프로세스를 죽인다.
            // 뱃지 하나 때문에 수업 중에 프로그램이 사라지면 안 된다.
        }
    }

    public static void HideBadge()
    {
        try { _current?.Hide(); }
        catch (Exception) { }
    }

    private void StartFadeTimer(double seconds)
    {
        _timer ??= new DispatcherTimer();
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        Hide();
    }

    public static void Dispose()
    {
        try { _current?.Close(); } catch (Exception) { }
        _current = null;
    }

    /// <summary>핸들을 만들고 활성화 불가 스타일을 입힌다. 창을 화면에 띄우지는 않는다.</summary>
    private void EnsureHandle()
    {
        if (_stylesApplied) return;

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        int ex = NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_NOACTIVATE
               | NativeMethods.WS_EX_TOOLWINDOW
               | NativeMethods.WS_EX_TRANSPARENT);

        _stylesApplied = true;
    }

    private void Place(Forms::Screen screen)
    {
        EnsureHandle();

        var b = screen.Bounds;

        // 화면을 다 덮으면 위압적이다. 짧은 변의 3분의 1 정도면 멀리서도 보인다.
        int side = Math.Max(160, Math.Min(b.Width, b.Height) / 3);
        int left = b.Left + (b.Width - side) / 2;
        int top = b.Top + (b.Height - side) / 2;

        _label.Text = OverlayTarget.DisplayNumber(screen).ToString();
        _label.FontSize = side * 0.5;

        // Show() 대신 SetWindowPos 로 띄운다. Show()는 포커스를 건드릴 수 있다.
        IntPtr handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, top, side, side,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        Visibility = Visibility.Visible;

        // 오버레이들과 같은 규약 — 물리 픽셀로 붙이고 루트에서 DPI를 되돌린다.
        var dpi = VisualTreeHelper.GetDpi(this);
        if (Content is FrameworkElement root)
            root.LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
    }
}
