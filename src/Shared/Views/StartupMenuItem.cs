using System.Windows.Forms;
using LectureHelper.Services;

namespace LectureHelper.Views;

/// <summary>The tray menu's "run at sign-in" checkbox, identical in both programs.</summary>
public static class StartupMenuItem
{
    public static ToolStripMenuItem Create(string name)
    {
        var item = new ToolStripMenuItem("Windows 시작 시 자동 실행")
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled(name),
        };

        // Wired after seeding Checked, so merely opening the menu never writes to the registry.
        item.CheckedChanged += (_, _) => StartupRegistration.SetEnabled(name, item.Checked);
        return item;
    }
}
