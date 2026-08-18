using System;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenNote.Views;

/// <summary>Always-on-top classroom countdown.</summary>
public partial class TimerWindow : Window
{
    private static readonly Brush IdleBackground = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x24, 0x29));
    private static readonly Brush FullscreenBackground = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x1C));
    private static readonly Brush AlarmBackground = new SolidColorBrush(Color.FromArgb(0xF2, 0xB3, 0x1E, 0x1E));
    private static readonly Brush NormalText = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xF8));
    private static readonly Brush WarningText = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x3A));

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _flash = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private TimeSpan _configured = TimeSpan.FromMinutes(5);
    private TimeSpan _remaining = TimeSpan.FromMinutes(5);
    private DateTime _deadline;
    private bool _running;
    private int _flashesLeft;
    private double _scale = 1.0;
    private bool _fullscreen;
    private Rect _windowedPlacement;

    public TimerWindow()
    {
        InitializeComponent();

        _tick.Tick += OnTick;
        _flash.Tick += OnFlash;

        BtnStart.Click += (_, _) => Toggle();
        BtnReset.Click += (_, _) => Reset();
        BtnClose.Click += (_, _) => Close();
        BtnFullscreen.Click += (_, _) => SetFullscreen(!_fullscreen);

        BtnP1.Click += (_, _) => SetPreset(TimeSpan.FromMinutes(1));
        BtnP3.Click += (_, _) => SetPreset(TimeSpan.FromMinutes(3));
        BtnP5.Click += (_, _) => SetPreset(TimeSpan.FromMinutes(5));
        BtnP10.Click += (_, _) => SetPreset(TimeSpan.FromMinutes(10));
        BtnMinus.Click += (_, _) => Adjust(TimeSpan.FromSeconds(-30));
        BtnPlus.Click += (_, _) => Adjust(TimeSpan.FromSeconds(30));

        Display.PreviewKeyDown += DisplayOnPreviewKeyDown;
        Display.LostKeyboardFocus += (_, _) => ApplyTypedTime();
        Display.GotKeyboardFocus += (_, _) => Display.SelectAll();

        Panel.MouseLeftButtonDown += OnPanelMouseDown;

        // The time box would otherwise take initial focus and swallow Space, R and F11.
        Loaded += (_, _) => Keyboard.Focus(this);

        Render();
    }

    // ------------------------------------------------------------ countdown

    private void Toggle()
    {
        StopFlashing();

        if (_running)
        {
            _remaining = _deadline - DateTime.UtcNow;
            if (_remaining < TimeSpan.Zero) _remaining = TimeSpan.Zero;
            _running = false;
            _tick.Stop();
        }
        else
        {
            ApplyTypedTime();
            if (_remaining <= TimeSpan.Zero) _remaining = _configured;
            if (_remaining <= TimeSpan.Zero) return;

            _deadline = DateTime.UtcNow + _remaining;
            _running = true;
            _tick.Start();
        }

        Render();
    }

    private void Reset()
    {
        StopFlashing();
        _running = false;
        _tick.Stop();
        _remaining = _configured;
        Render();
    }

    private void SetPreset(TimeSpan value)
    {
        StopFlashing();
        _configured = value;
        _remaining = value;

        if (_running) _deadline = DateTime.UtcNow + value;
        Render();
    }

    private void Adjust(TimeSpan delta)
    {
        StopFlashing();

        var next = (_running ? _deadline - DateTime.UtcNow : _remaining) + delta;
        if (next < TimeSpan.Zero) next = TimeSpan.Zero;
        if (next > TimeSpan.FromHours(9)) next = TimeSpan.FromHours(9);

        _remaining = next;
        _configured = next;
        if (_running) _deadline = DateTime.UtcNow + next;

        Render();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining = _deadline - DateTime.UtcNow;

        if (_remaining <= TimeSpan.Zero)
        {
            _remaining = TimeSpan.Zero;
            _running = false;
            _tick.Stop();
            StartAlarm();
        }

        Render();
    }

    private void StartAlarm()
    {
        _flashesLeft = 16;
        _flash.Start();
        SystemSounds.Exclamation.Play();
    }

    private void OnFlash(object? sender, EventArgs e)
    {
        _flashesLeft--;
        if (_flashesLeft <= 0)
        {
            StopFlashing();
            return;
        }

        bool lit = _flashesLeft % 2 == 0;
        Panel.Background = lit ? AlarmBackground : RestingBackground;
        if (lit && _flashesLeft % 6 == 0) SystemSounds.Exclamation.Play();
    }

    private void StopFlashing()
    {
        _flash.Stop();
        _flashesLeft = 0;
        Panel.Background = RestingBackground;
    }

    private void Render()
    {
        var shown = _remaining < TimeSpan.Zero ? TimeSpan.Zero : _remaining;
        int totalMinutes = (int)shown.TotalMinutes;
        int seconds = shown.Seconds;

        // Round up so a running clock never displays 00:00 while a fraction of a second remains.
        if (_running && shown.Milliseconds > 0)
        {
            var ceil = TimeSpan.FromSeconds(Math.Ceiling(shown.TotalSeconds));
            totalMinutes = (int)ceil.TotalMinutes;
            seconds = ceil.Seconds;
        }

        Display.Text = $"{totalMinutes:00}:{seconds:00}";
        Display.IsReadOnly = _running;
        Display.Foreground = shown.TotalSeconds <= 10 && shown > TimeSpan.Zero ? WarningText : NormalText;

        BtnStart.Content = _running ? "일시정지" : "시작";
        BtnStart.Background = _running
            ? new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x14))
            : new SolidColorBrush(Color.FromRgb(0x1E, 0x7F, 0x4A));
    }

    // -------------------------------------------------------------- editing

    private void DisplayOnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyTypedTime();
            Keyboard.ClearFocus();
            Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Render();
            Focus();
        }
    }

    private void ApplyTypedTime()
    {
        if (_running) return;

        var parsed = ParseTime(Display.Text);
        if (parsed is null)
        {
            Render();
            return;
        }

        _configured = parsed.Value;
        _remaining = parsed.Value;
        Render();
    }

    private static TimeSpan? ParseTime(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        if (text.Contains(':'))
        {
            string[] parts = text.Split(':');
            if (parts.Length != 2) return null;
            if (!int.TryParse(parts[0], out int m) || !int.TryParse(parts[1], out int s)) return null;
            if (m < 0 || s < 0 || s > 59) return null;
            return Cap(TimeSpan.FromSeconds(m * 60 + s));
        }

        if (!int.TryParse(text, out int minutes) || minutes < 0) return null;
        return Cap(TimeSpan.FromMinutes(minutes));
    }

    private static TimeSpan Cap(TimeSpan value) => value > TimeSpan.FromHours(9) ? TimeSpan.FromHours(9) : value;

    // ------------------------------------------------------------ fullscreen

    /// <summary>
    /// Fullscreen drops the controls and blows the digits up, for "쉬는 시간 5분" moments where the
    /// screen belongs to the clock. The windowed form stays the default so the timer can sit in a
    /// corner while the lesson carries on.
    /// </summary>
    /// <summary>Windowed sits over the lesson so it stays translucent; fullscreen owns the screen.</summary>
    private Brush RestingBackground => _fullscreen ? FullscreenBackground : IdleBackground;

    private void SetFullscreen(bool on)
    {
        if (_fullscreen == on) return;
        _fullscreen = on;
        Panel.Background = RestingBackground;

        if (on)
        {
            _windowedPlacement = new Rect(Left, Top, ActualWidth, ActualHeight);

            SizeToContent = SizeToContent.Manual;
            Root.LayoutTransform = Transform.Identity;
            Panel.CornerRadius = new CornerRadius(0);
            Panel.BorderThickness = new Thickness(0);
            Panel.Padding = new Thickness(0);

            Caption.Visibility = Visibility.Collapsed;
            Presets.Visibility = Visibility.Collapsed;
            Controls.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Collapsed;
            FullscreenIcon.Data = Geometry.Parse("M8,4 V8 H4 M14,8 H18 V4 M18,14 H14 V18 M4,14 H8 V18");

            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point((int)(Left + ActualWidth / 2), (int)(Top + ActualHeight / 2)));
            var dpi = VisualTreeHelper.GetDpi(this);

            Left = screen.Bounds.Left / dpi.DpiScaleX;
            Top = screen.Bounds.Top / dpi.DpiScaleY;
            Width = screen.Bounds.Width / dpi.DpiScaleX;
            Height = screen.Bounds.Height / dpi.DpiScaleY;

            Display.FontSize = Height * 0.55;
        }
        else
        {
            Panel.CornerRadius = new CornerRadius(10);
            Panel.BorderThickness = new Thickness(1);
            Panel.Padding = new Thickness(14, 10, 14, 12);

            Caption.Visibility = Visibility.Visible;
            Presets.Visibility = Visibility.Visible;
            Controls.Visibility = Visibility.Visible;
            Hint.Visibility = Visibility.Visible;
            FullscreenIcon.Data = Geometry.Parse("M4,8 V4 H8 M14,4 H18 V8 M18,14 V18 H14 M8,18 H4 V14");

            Display.FontSize = 64;
            SizeToContent = SizeToContent.WidthAndHeight;
            Root.LayoutTransform = new ScaleTransform(_scale, _scale);

            Left = _windowedPlacement.X;
            Top = _windowedPlacement.Y;
        }

        BtnFullscreen.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------- interaction

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBox) return;
        if (e.ButtonState != MouseButtonState.Pressed) return;

        StopFlashing();
        if (_fullscreen) return;

        DragMove();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_fullscreen) return;

        _scale = Math.Clamp(_scale + Math.Sign(e.Delta) * 0.1, 0.6, 3.5);
        Root.LayoutTransform = new ScaleTransform(_scale, _scale);
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // While typing a time, let the box have the letters — but never the window-level keys.
        if (Display.IsKeyboardFocusWithin && e.Key is not (Key.Escape or Key.F11)) return;

        switch (e.Key)
        {
            case Key.Space: Toggle(); break;
            case Key.R: Reset(); break;
            case Key.F11: SetFullscreen(!_fullscreen); break;
            case Key.Escape:
                if (_fullscreen) SetFullscreen(false);
                else Close();
                break;
            default: return;
        }

        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        _flash.Stop();
        base.OnClosed(e);
    }
}
