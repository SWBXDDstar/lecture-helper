using System;
using Microsoft.Win32;

namespace LectureHelper.Services;

/// <summary>
/// "Run when Windows starts", registered per user under HKCU so it never needs admin rights and
/// never touches other accounts on a shared classroom machine.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Path of the running exe. Null only in odd hosting cases where there is no exe.</summary>
    public static string? ExecutablePath => Environment.ProcessPath;

    public static bool IsEnabled(string name) => ReadValue(name) is not null;

    public static void SetEnabled(string name, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        if (!enabled)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        if (ExecutablePath is { } path) key.SetValue(name, Quote(path));
    }

    /// <summary>
    /// Rewrites the stored path when the exe has moved. These programs live in a synced folder, so
    /// the registered command can silently go stale after a reorganise.
    /// </summary>
    public static void RefreshIfMoved(string name)
    {
        if (ExecutablePath is not { } path) return;

        string? stored = ReadValue(name);
        if (stored is null) return;

        if (!string.Equals(stored, Quote(path), StringComparison.OrdinalIgnoreCase))
            SetEnabled(name, true);
    }

    private static string? ReadValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) as string;
    }

    private static string Quote(string path) => $"\"{path}\"";
}
