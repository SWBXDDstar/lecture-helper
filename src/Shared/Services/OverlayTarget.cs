using System;
using System.Collections.Generic;
using System.Linq;
using LectureHelper.Native;
using Forms = System.Windows.Forms;

namespace LectureHelper.Services;

/// <summary>
/// Which monitor ScreenNote's overlays cover.
///
/// 교실 PC는 보통 모니터가 둘이다 — 하나는 학생에게 보여주는 강의 화면, 하나는 채팅·자료를 보는
/// 읽기 화면. 예전에는 오버레이가 가상 화면 전체를 덮어서 주석을 다는 동안 읽기 화면까지
/// 얼어붙었다. 그래서 한 대만 덮는 것을 기본으로 한다.
/// </summary>
public static class OverlayTarget
{
    /// <summary>고정된 모니터의 장치 이름. 비어 있으면 커서가 있는 모니터를 따라간다.</summary>
    public const string FollowCursor = "";

    /// <summary>모든 모니터를 한 번에 덮던 옛 동작. 화면이 하나뿐이면 사실상 같다.</summary>
    public const string AllMonitors = "*";

    /// <summary>
    /// 지금 오버레이를 띄울 영역. 고정된 모니터가 사라졌으면(케이블을 뽑았거나 해상도가 바뀌었거나)
    /// 조용히 커서 쪽으로 돌아간다 — 수업 중에 아무것도 안 뜨는 것이 제일 나쁘다.
    /// </summary>
    public static NativeMethods.RECT Resolve(string setting)
    {
        if (setting == AllMonitors) return NativeMethods.GetVirtualScreen();

        if (!string.IsNullOrEmpty(setting))
        {
            var pinned = Forms::Screen.AllScreens.FirstOrDefault(s => s.DeviceName == setting);
            if (pinned is not null) return ToRect(pinned.Bounds);
        }

        return ToRect(CursorScreen().Bounds);
    }

    public static Forms::Screen CursorScreen()
    {
        NativeMethods.GetCursorPos(out var p);
        return Forms::Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
    }

    /// <summary>트레이 메뉴에 보여줄 이름. "1번 모니터 (1920×1080, 주 모니터)" 꼴.</summary>
    public static string Describe(Forms::Screen screen)
    {
        int index = Array.IndexOf(Forms::Screen.AllScreens, screen) + 1;
        string primary = screen.Primary ? ", 주 모니터" : "";
        return $"{index}번 모니터 ({screen.Bounds.Width}×{screen.Bounds.Height}{primary})";
    }

    /// <summary>설정값 하나를 사람이 읽는 한 줄로. 트레이 메뉴 제목에 쓴다.</summary>
    public static string DescribeSetting(string setting)
    {
        if (setting == AllMonitors) return "모든 모니터";
        if (string.IsNullOrEmpty(setting)) return "마우스가 있는 모니터";

        var pinned = Forms::Screen.AllScreens.FirstOrDefault(s => s.DeviceName == setting);
        // 고정해둔 모니터가 지금 안 붙어 있으면 그렇게 말해준다. 조용히 다르게 도는 것보다 낫다.
        return pinned is not null ? Describe(pinned) : "고정한 모니터 없음 → 마우스 따라감";
    }

    public static IReadOnlyList<Forms::Screen> Monitors => Forms::Screen.AllScreens;

    private static NativeMethods.RECT ToRect(System.Drawing.Rectangle r) => new()
    {
        Left = r.Left,
        Top = r.Top,
        Right = r.Right,
        Bottom = r.Bottom,
    };
}
