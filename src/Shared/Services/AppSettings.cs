using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LectureHelper.Services;

/// <summary>
/// The handful of preferences worth surviving a restart. Kept in HKCU alongside the startup entry,
/// so there is no settings file to lose when the program folder moves.
/// </summary>
public static class AppSettings
{
    private const string Key = @"Software\LectureHelper";
    private const string SaveFolderValue = "SaveFolder";
    private const string OverlayMonitorValue = "OverlayMonitor";

    /// <summary>
    /// ScreenNote 오버레이가 덮을 모니터의 장치 이름.
    /// 빈 값이면 마우스가 있는 모니터, "*"면 전부. 자세한 것은 <see cref="OverlayTarget"/>.
    /// </summary>
    public static string OverlayMonitor
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(OverlayMonitorValue) as string ?? OverlayTarget.FollowCursor;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key?.SetValue(OverlayMonitorValue, value);
        }
    }

    /// <summary>Where captures are written. Downloads by default — the folder people already empty.</summary>
    public static string SaveFolder
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue(SaveFolderValue) is string stored && stored.Length > 0) return stored;
            return DownloadsFolder;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key?.SetValue(SaveFolderValue, value);
        }
    }

    /// <summary>Creates the folder if needed, falling back to Downloads when the stored one is gone.</summary>
    public static string EnsureSaveFolder()
    {
        string folder = SaveFolder;
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A folder on a disconnected drive or a since-deleted path should not block saving.
            folder = DownloadsFolder;
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string DownloadsFolder
    {
        get
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                var id = FolderIdDownloads;
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out buffer) == 0)
                {
                    string? path = Marshal.PtrToStringUni(buffer);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch (DllNotFoundException)
            {
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
            }

            // Downloads has no Environment.SpecialFolder, so fall back to the conventional location.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
