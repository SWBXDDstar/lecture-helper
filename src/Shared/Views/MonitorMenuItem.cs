using System;
using System.Windows.Forms;
using LectureHelper.Services;

namespace LectureHelper.Views;

/// <summary>
/// 어느 모니터를 덮을지 고르는 트레이 메뉴. 두 프로그램이 같이 쓴다 —
/// ScreenNote 는 오버레이가 덮을 화면, ScreenCapture 는 캡처할 화면.
/// </summary>
public static class MonitorMenuItem
{
    /// <param name="title">"오버레이 화면" 처럼 메뉴 앞에 붙는 이름.</param>
    /// <param name="get">지금 설정값을 읽어오는 함수.</param>
    /// <param name="set">고른 값을 저장하는 함수.</param>
    public static ToolStripMenuItem Create(string title, Func<string> get, Action<string> set)
    {
        var root = new ToolStripMenuItem { Text = Label(title, get) };

        // 모니터는 연결이 바뀌므로 목록은 메뉴를 펼칠 때마다 새로 만든다.
        root.DropDownOpening += (_, _) =>
        {
            root.DropDownItems.Clear();
            string current = get();

            void Add(string text, string value, Screen? confirmOn = null)
            {
                var item = new ToolStripMenuItem(text) { Checked = value == current };
                item.Click += (_, _) =>
                {
                    set(value);
                    root.Text = Label(title, get);

                    // 고른 화면에 번호를 잠깐 띄워 "이 화면이 맞나"를 확인시켜 준다.
                    // 메뉴가 닫힌 뒤에 뜨므로 메뉴를 건드리지 않는다 — 자세한 것은 MonitorBadge 주석.
                    if (confirmOn is not null) MonitorBadge.Flash(confirmOn);
                };
                root.DropDownItems.Add(item);
            }

            Add("마우스가 있는 모니터", OverlayTarget.FollowCursor);
            root.DropDownItems.Add(new ToolStripSeparator());

            foreach (var screen in OverlayTarget.Monitors)
                Add(OverlayTarget.Describe(screen), screen.DeviceName, screen);

            root.DropDownItems.Add(new ToolStripSeparator());
            Add("모든 모니터", OverlayTarget.AllMonitors);
        };

        return root;
    }

    /// <summary>메뉴 글자에 현재 값을 같이 보여줘서 열어보지 않아도 확인되게 한다.</summary>
    private static string Label(string title, Func<string> get) =>
        $"{title}:  {OverlayTarget.DescribeSetting(get())}";
}
