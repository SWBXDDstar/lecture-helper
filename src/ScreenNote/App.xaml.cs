using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using LectureHelper.Native;
using LectureHelper.Services;
using ScreenNote.Views;
using Forms = System.Windows.Forms;

namespace ScreenNote;

public partial class App : Application
{
    private const uint VK_1 = 0x31;
    private const uint VK_2 = 0x32;
    private const uint VK_3 = 0x33;

    private const string StartupName = "ScreenNote";

    private static Mutex? _instanceMutex;

    private HotkeyService? _hotkeys;
    private Forms::NotifyIcon? _tray;
    private ZoomWindow? _zoom;
    private AnnotateWindow? _annotate;
    private TimerWindow? _timer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, "LectureHelper.ScreenNote.SingleInstance", out bool isOwner);
        if (!isOwner)
        {
            MessageBox.Show("ScreenNote가 이미 실행 중입니다. 작업 표시줄 오른쪽 트레이를 확인하세요.",
                "ScreenNote", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log("UNHANDLED: " + args.Exception);
            MessageBox.Show($"오류가 발생했습니다.\n\n{args.Exception.Message}\n\n로그: {LogPath}",
                "ScreenNote", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        StartupRegistration.RefreshIfMoved(StartupName);
        SetupTray();
        SetupHotkeys();
    }

    internal static string LogPath => Path.Combine(Path.GetTempPath(), "ScreenNote.log");

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private void SetupTray()
    {
        var menu = new Forms::ContextMenuStrip();
        menu.Items.Add("확대  (Ctrl+1)", null, (_, _) => StartZoom());
        menu.Items.Add("화면 주석  (Ctrl+2)", null, (_, _) => StartAnnotate());
        menu.Items.Add("수업 타이머  (Ctrl+3)", null, (_, _) => ToggleTimer());
        menu.Items.Add(new Forms::ToolStripSeparator());
        menu.Items.Add(BuildMonitorItem());
        menu.Items.Add(LectureHelper.Views.StartupMenuItem.Create(StartupName));
        menu.Items.Add("단축키 안내", null, (_, _) => ShowHelp());
        menu.Items.Add("종료", null, (_, _) => Shutdown());

        _tray = new Forms::NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Text = "ScreenNote — 확대 · 화면 주석 · 타이머",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms::MouseButtons.Left) StartAnnotate();
        };
    }

    /// <summary>
    /// 확대·주석이 어느 모니터를 덮을지 고르는 메뉴.
    /// 모니터는 연결이 바뀌므로 목록은 메뉴를 펼칠 때마다 새로 만든다.
    /// </summary>
    private static Forms::ToolStripMenuItem BuildMonitorItem()
    {
        var root = new Forms::ToolStripMenuItem { Text = MonitorLabel() };

        root.DropDownOpening += (_, _) =>
        {
            root.DropDownItems.Clear();
            string current = AppSettings.OverlayMonitor;

            void Add(string text, string value)
            {
                var item = new Forms::ToolStripMenuItem(text) { Checked = value == current };
                item.Click += (_, _) =>
                {
                    AppSettings.OverlayMonitor = value;
                    root.Text = MonitorLabel();
                };
                root.DropDownItems.Add(item);
            }

            Add("마우스가 있는 모니터", OverlayTarget.FollowCursor);
            root.DropDownItems.Add(new Forms::ToolStripSeparator());

            foreach (var screen in OverlayTarget.Monitors)
                Add(OverlayTarget.Describe(screen), screen.DeviceName);

            root.DropDownItems.Add(new Forms::ToolStripSeparator());
            Add("모든 모니터 (예전 방식)", OverlayTarget.AllMonitors);
        };

        return root;
    }

    private static string MonitorLabel() =>
        $"오버레이 화면:  {OverlayTarget.DescribeSetting(AppSettings.OverlayMonitor)}";

    private void SetupHotkeys()
    {
        _hotkeys = new HotkeyService();

        bool zoom = _hotkeys.Register(NativeMethods.MOD_CONTROL, VK_1, StartZoom);
        bool annotate = _hotkeys.Register(NativeMethods.MOD_CONTROL, VK_2, StartAnnotate);
        bool timer = _hotkeys.Register(NativeMethods.MOD_CONTROL, VK_3, ToggleTimer);

        string message = zoom && annotate && timer
            ? "Ctrl+1 확대 · Ctrl+2 화면 주석 · Ctrl+3 타이머"
            : "일부 단축키를 다른 프로그램이 쓰고 있습니다. 트레이 메뉴로 실행하세요.";

        _tray?.ShowBalloonTip(3000, "ScreenNote 실행 중", message, Forms::ToolTipIcon.Info);
    }

    /// <summary>Only one full-screen overlay may be up at a time; they all grab the whole desktop.</summary>
    private bool OverlayBusy => _zoom is not null || _annotate is not null;

    private void StartZoom()
    {
        if (OverlayBusy) return;

        try
        {
            _zoom = new ZoomWindow();
            _zoom.Closed += (_, _) => _zoom = null;
            _zoom.Show();
            _zoom.Activate();
        }
        catch (Exception ex)
        {
            _zoom = null;
            Log("StartZoom: " + ex);
        }
    }

    private void StartAnnotate()
    {
        if (OverlayBusy) return;

        try
        {
            _annotate = new AnnotateWindow();
            _annotate.Closed += (_, _) => _annotate = null;
            _annotate.Show();
            _annotate.Activate();
        }
        catch (Exception ex)
        {
            _annotate = null;
            Log("StartAnnotate: " + ex);
        }
    }

    private void ToggleTimer()
    {
        if (_timer is not null)
        {
            _timer.Close();
            return;
        }

        _timer = new TimerWindow();
        _timer.Closed += (_, _) => _timer = null;
        _timer.Show();
    }

    private static void ShowHelp()
    {
        MessageBox.Show(
            """
            [전역 단축키]
              Ctrl+1   확대        화면을 정지시키고 커서를 따라 확대
              Ctrl+2   화면 주석  화면에 바로 그리기
              Ctrl+3   수업 타이머

            [듀얼 모니터]
              확대·주석은 모니터 한 대만 덮습니다. 옆 화면의 채팅·자료는 그대로 보입니다.
              기본은 마우스가 있는 모니터이고, 트레이 메뉴 "오버레이 화면"에서 고정할 수 있습니다.

            [확대 화면에서]
              마우스 이동       확대 지점 이동
              휠                배율 조절 (100%~800%)
              위 / 아래 방향키  배율 조절
              클릭              그 자리에 고정하고 그리기 시작
              Space             다시 마우스를 따라가게
              Ctrl+휠           선 두께
              Esc               나가기

            [그리기 — ScreenCapture와 동일한 단축키]
              드래그            펜
              Ctrl+드래그       네모
              Tab+드래그        동그라미
              Shift+드래그      직선
              Ctrl+Shift+드래그 화살표
              마우스 휠         선 두께
              R G B Y P         색상 (빨강 초록 파랑 노랑 분홍)

            [도구 — 같은 키를 다시 누르면 펜으로 돌아옴]
              H                 형광펜    수식키가 그대로 먹습니다.
                                          Shift+드래그 = 곧게 그은 형광펜 (글줄 강조)
                                          Ctrl+드래그  = 네모 박스 강조
              M                 모자이크   드래그한 사각형만 계산합니다
              Shift+M           흐리게     휠로 세기 조절
              N                 숫자 마커  클릭할 때마다 1, 2, 3...
              T                 텍스트     클릭한 자리에 입력, Enter 확정, 휠로 글자 크기

            [도장 — 클릭한 자리에 하나씩 찍힘, 휠로 크기]
              Q                 물음표     "여기 왜?"
              S                 별         "이거 중요"
              V                 체크       "이건 맞음"
              X                 엑스       "이건 틀림"
              O                 동그라미   "이건 정답"

            [그 밖에]
              E                 전체 지우기
              Ctrl+Z / Ctrl+Y   실행 취소 / 다시 실행
              오른쪽 클릭       그리던 것 취소 → 도구 해제 → 나가기
              Esc               도구 해제 → 나가기
              F1                단축키 안내 다시 보기

            모자이크로 답을 가려놓고 설명한 뒤 Ctrl+Z 로 걷어내면 정답 공개가 됩니다.

            [타이머]
              1분 / 3분 / 5분 / 10분 프리셋, ±30초
              시간을 클릭해 직접 입력 — 7 은 7분, 2:30 은 2분 30초
              Space  시작 / 일시정지      R  리셋
              F11    전체화면 전환        Esc  닫기
              드래그 이동, 휠 크기 조절
            """,
            "ScreenNote 단축키", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static Icon BuildTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new SolidBrush(System.Drawing.Color.FromArgb(255, 43, 47, 54));
            g.FillEllipse(background, 0, 0, 31, 31);

            using var lens = new Pen(System.Drawing.Color.FromArgb(255, 120, 190, 255), 2.6f);
            g.DrawEllipse(lens, 7, 7, 13, 13);
            g.DrawLine(lens, 19, 19, 25, 25);

            using var ink = new Pen(System.Drawing.Color.FromArgb(255, 240, 70, 70), 2.6f);
            g.DrawLine(ink, 10, 22, 16, 14);
        }

        IntPtr handle = bitmap.GetHicon();
        return (Icon)Icon.FromHandle(handle).Clone();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
