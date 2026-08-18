using System;
using System.Collections.Generic;
using System.Windows.Interop;

namespace LectureHelper.Native;

/// <summary>Owns a message-only window so global hotkeys work while no UI window exists.</summary>
public sealed class HotkeyService : IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("ScreenNoteHotkeySink")
        {
            ParentWindow = HWND_MESSAGE,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Returns false when another application already owns the combination.</summary>
    public bool Register(uint modifiers, uint virtualKey, Action handler)
    {
        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey))
            return false;

        _handlers[id] = handler;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            handler();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (int id in _handlers.Keys)
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
