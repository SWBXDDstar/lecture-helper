using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using LectureHelper.Native;
using LectureHelper.Services;
using ScreenCapture.Views;
using Forms = System.Windows.Forms;

namespace ScreenCapture;

public partial class App : Application
{
    private const uint VK_S = 0x53;
    private const uint VK_F1 = 0x70;
    private const uint VK_F3 = 0x72;

    private const string StartupName = "ScreenCapture";

    private static Mutex? _instanceMutex;

    private HotkeyService? _hotkeys;
    private Forms::NotifyIcon? _tray;
    private CaptureWindow? _capture;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, "LectureHelper.ScreenCapture.SingleInstance", out bool isOwner);
        if (!isOwner)
        {
            MessageBox.Show("ScreenCapture가 이미 실행 중입니다. 작업 표시줄 오른쪽 트레이를 확인하세요.",
                "ScreenCapture", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log("UNHANDLED: " + args.Exception);
            MessageBox.Show($"오류가 발생했습니다.\n\n{args.Exception.Message}\n\n로그: {LogPath}",
                "ScreenCapture", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        StartupRegistration.RefreshIfMoved(StartupName);
        SetupTray();
        SetupHotkeys();
    }

    /// <summary>
    /// Tray balloon used to confirm a silent save. Static so the capture overlay can report after
    /// it has already closed itself.
    /// </summary>
    internal static void Notify(string title, string message, bool warning = false)
    {
        if (Current is App app && app._tray is { } tray)
            tray.ShowBalloonTip(4000, title, message,
                warning ? Forms::ToolTipIcon.Warning : Forms::ToolTipIcon.Info);
    }

    internal static string LogPath => Path.Combine(Path.GetTempPath(), "ScreenCapture.log");

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
        menu.Items.Add("화면 캡처  (Ctrl+F1)", null, (_, _) => StartCapture());
        menu.Items.Add("클립보드 이미지 고정  (F3)", null, (_, _) => PinFromClipboard());
        menu.Items.Add(new Forms::ToolStripSeparator());
        menu.Items.Add(BuildSaveFolderItem());
        menu.Items.Add(LectureHelper.Views.StartupMenuItem.Create(StartupName));
        menu.Items.Add("단축키 안내", null, (_, _) => ShowHelp());
        menu.Items.Add("종료", null, (_, _) => Shutdown());

        _tray = new Forms::NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Text = "ScreenCapture — 캡처 · 모자이크 · 화면 고정",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms::MouseButtons.Left) StartCapture();
        };
    }

    /// <summary>Shows the current folder in the label so it can be checked without opening anything.</summary>
    private static Forms::ToolStripMenuItem BuildSaveFolderItem()
    {
        var item = new Forms::ToolStripMenuItem { Text = SaveFolderLabel() };

        item.Click += (_, _) =>
        {
            using var picker = new Forms::FolderBrowserDialog
            {
                Description = "캡처를 저장할 폴더를 고르세요.",
                UseDescriptionForTitle = true,
                SelectedPath = AppSettings.SaveFolder,
                ShowNewFolderButton = true,
            };

            if (picker.ShowDialog() != Forms::DialogResult.OK) return;

            AppSettings.SaveFolder = picker.SelectedPath;
            item.Text = SaveFolderLabel();
            Notify("저장 폴더를 바꿨습니다", picker.SelectedPath);
        };

        return item;
    }

    private static string SaveFolderLabel()
    {
        string folder = AppSettings.SaveFolder;
        string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        return $"저장 폴더:  {(name.Length > 0 ? name : folder)}…";
    }

    private void SetupHotkeys()
    {
        _hotkeys = new HotkeyService();

        // Ctrl+F1 rather than bare F1: grabbing F1 system-wide would keep it from reaching the
        // overlays, which would push the shortcut hint onto F2 - a key that must stay free.
        bool ctrlF1 = _hotkeys.Register(NativeMethods.MOD_CONTROL, VK_F1, StartCapture);
        _hotkeys.Register(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VK_S, StartCapture);
        _hotkeys.Register(0, VK_F3, PinFromClipboard);

        _tray?.ShowBalloonTip(3000, "ScreenCapture 실행 중",
            ctrlF1
                ? "Ctrl+F1 로 캡처를 시작하세요."
                : "Ctrl+F1 을 다른 프로그램이 쓰고 있습니다. Ctrl+Shift+S 를 쓰세요.",
            Forms::ToolTipIcon.Info);
    }

    private void StartCapture()
    {
        if (_capture is not null) return;

        try
        {
            _capture = new CaptureWindow();
            _capture.Closed += (_, _) => _capture = null;
            _capture.Show();
            _capture.Activate();
        }
        catch (Exception ex)
        {
            _capture = null;
            MessageBox.Show($"캡처를 시작하지 못했습니다.\n\n{ex.Message}", "ScreenCapture",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PinFromClipboard()
    {
        BitmapSource? image = null;
        try
        {
            if (Clipboard.ContainsImage()) image = Clipboard.GetImage();
        }
        catch
        {
            // Clipboard access can transiently fail while another app holds it.
        }

        if (image is null)
        {
            _tray?.ShowBalloonTip(2000, "ScreenCapture", "클립보드에 이미지가 없습니다.", Forms::ToolTipIcon.Warning);
            return;
        }

        PinWindow.Pin(image, null);
    }

    private static void ShowHelp()
    {
        MessageBox.Show(
            """
            [전역 단축키]
              Ctrl+F1  또는  Ctrl+Shift+S   화면 캡처 시작
              F3                             클립보드 이미지를 화면에 고정

            [영역 선택]
              드래그            영역 선택 (드래그 없이 클릭하면 창 전체 선택)
              Alt+드래그        선택 영역 이동
              테두리 핸들       선택 영역 크기 조절
              Ctrl+A            화면 전체 선택
              C                 커서 위치의 색상 코드 복사

            [그리기 — ScreenNote와 동일한 단축키]
              드래그            펜
              Ctrl+드래그       네모
              Tab+드래그        동그라미
              Shift+드래그      직선
              Ctrl+Shift+드래그 화살표
              마우스 휠         선 두께
              R G B O Y P       색상 (빨강 초록 파랑 주황 노랑 분홍)
              T                 텍스트
              E                 전체 지우기
              Ctrl+Z / Ctrl+Y   실행 취소 / 다시 실행
              F1                단축키 안내 다시 보기

            [도구 — 같은 키를 다시 누르면 해제]
              H                 형광펜
              M                 모자이크        Shift+M  흐리게
              N                 숫자 마커

            [도장 — 클릭한 자리에 하나씩 찍힘, 휠로 크기]
              Q                 물음표
              S                 별
              V                 체크

            [마무리]
              Ctrl+C 또는 Enter 복사하고 닫기
              Ctrl+S            저장 폴더에 바로 저장 (대화상자 없음)
              Ctrl+Alt+S        다른 위치를 골라 저장
              Ctrl+D            선택 영역을 화면에 고정
              Esc               취소

            저장 폴더는 기본이 [다운로드] 이고, 트레이 메뉴에서 바꿀 수 있습니다.
            어디에 저장됐는지는 저장 직후 알림으로 알려줍니다.

            [고정된 이미지에서]
              드래그            이동
              휠                확대 / 축소
              Ctrl+휠           투명도 조절
              마우스 오른쪽     메뉴 (복사 / 저장 / 원본 크기 / 닫기)
              Esc               닫기
            """,
            "ScreenCapture 단축키", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static Icon BuildTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new SolidBrush(System.Drawing.Color.FromArgb(255, 43, 47, 54));
            g.FillEllipse(background, 0, 0, 31, 31);

            using var frame = new Pen(System.Drawing.Color.FromArgb(255, 120, 190, 255), 2.4f);
            g.DrawRectangle(frame, 8, 10, 16, 12);
            using var accent = new SolidBrush(System.Drawing.Color.FromArgb(255, 240, 70, 70));
            g.FillRectangle(accent, 12, 14, 8, 4);
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
