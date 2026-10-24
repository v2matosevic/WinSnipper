using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinSnipper;

/// <summary>
/// Small always-on-top control pill shown while recording (elapsed time,
/// pause, stop), plus four thin border strips around the recorded region.
/// Controls stay on a real monitor even for fullscreen recordings. WGC can
/// exclude the embedded controls; compatibility capture leaves them visible
/// rather than drawing a protected black rectangle over the recorded game.
/// </summary>
public partial class RecordingHud : Window
{
    private readonly Int32Rect _region;
    private readonly DispatcherTimer _timer;
    private readonly Func<TimeSpan> _elapsed;
    private readonly List<Window> _strips = new();

    public event Action? StopRequested;
    public event Action<bool>? PauseToggled; // true = now paused

    public bool ControlsMayBeRecorded { get; private set; }
    private bool _paused;
    private bool _captureExcludesWindows;
    private bool _userMoved;
    private readonly object _affinityGate = new();
    private IntPtr _nativeHandle;
    private readonly Func<bool>? _captureExclusionSupported;

    public RecordingHud(Int32Rect regionPx, Func<TimeSpan> elapsed, bool captureExcludesWindows = false,
        Func<bool>? captureExclusionSupported = null)
    {
        InitializeComponent();
        _region = regionPx;
        _elapsed = elapsed;
        _captureExcludesWindows = captureExcludesWindows;
        _captureExclusionSupported = captureExclusionSupported;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            var t = _elapsed();
            string text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
            if (TimeText.Text != text) TimeText.Text = text;
        };
        _timer.Start();

        Loaded += (_, _) => PositionControls();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke((Action)PositionControls);

        Closed += (_, _) =>
        {
            lock (_affinityGate) _nativeHandle = IntPtr.Zero;
            _timer.Stop();
            foreach (var s in _strips) s.Close();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        lock (_affinityGate) _nativeHandle = hwnd;
        const int GWL_EXSTYLE = -20;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, GetWindowLongPtr(hwnd, GWL_EXSTYLE) | 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */);
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowMessage);
        ShowBorderStrips();
        PositionControls();
    }

    public void SetCaptureMode(bool excludesWindows)
    {
        lock (_affinityGate) _captureExcludesWindows = excludesWindows;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
            ApplyAffinity(new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }

    /// <summary>Native-only: called on the recorder thread before fallback pixels.
    /// Never wait for WPF here; StopAsync may already be waiting for the encoder.</summary>
    public void PrepareCompatibilityCapture()
    {
        lock (_affinityGate)
        {
            _captureExcludesWindows = false;
            if (_nativeHandle != IntPtr.Zero) SetWindowDisplayAffinity(_nativeHandle, 0);
        }
    }

    public void Reveal()
    {
        _userMoved = false;
        PositionControls();
        Topmost = false;
        Topmost = true;
    }

    private void PositionControls()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var monitor = Monitors.FromRect(_region.X, _region.Y, _region.Width, _region.Height);
        var work = new Int32Rect(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
        int w = (int)Math.Ceiling(DesiredSize.Width * monitor.Scale);
        int h = (int)Math.Ceiling(DesiredSize.Height * monitor.Scale);
        Int32Rect bounds;
        if (_userMoved && GetWindowRect(hwnd, out var old))
        {
            monitor = Monitors.FromRect(old.Left, old.Top, old.Right - old.Left, old.Bottom - old.Top);
            work = new Int32Rect(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
            w = (int)Math.Ceiling(DesiredSize.Width * monitor.Scale);
            h = (int)Math.Ceiling(DesiredSize.Height * monitor.Scale);
            bounds = RecordingHudLayout.Clamp(new Int32Rect(old.Left, old.Top, w, h), work);
        }
        else bounds = RecordingHudLayout.Place(_region, work, w, h);
        ApplyAffinity(bounds);
        MoveWindow(hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
    }

    private void ApplyAffinity(Int32Rect bounds)
    {
        lock (_affinityGate)
        {
            bool inside = RecordingHudLayout.Intersects(bounds, _region);
            // Read the recorder's current mode while holding the same gate as its
            // native fallback clear. A queued/stale UI update cannot re-protect
            // an embedded bar after the worker has already switched to GDI.
            bool exclude = !inside || (_captureExcludesWindows && (_captureExclusionSupported?.Invoke() ?? true));
            var hwnd = _nativeHandle;
            bool applied = SetWindowDisplayAffinity(hwnd, exclude ? 0x11u : 0u);
            if (!applied && exclude) SetWindowDisplayAffinity(hwnd, 0);
            ControlsMayBeRecorded = inside && (!exclude || !applied);
        }
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021 /* WM_MOUSEACTIVATE */)
        {
            handled = true;
            return (IntPtr)3; // MA_NOACTIVATE: buttons must not take focus away from a fullscreen game.
        }
        if (message == 0x0216 /* WM_MOVING */)
        {
            var r = Marshal.PtrToStructure<RECT>(lParam);
            var monitor = Monitors.FromRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            var bounds = RecordingHudLayout.Clamp(new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                new Int32Rect(monitor.Left, monitor.Top, monitor.Width, monitor.Height));
            _userMoved = true;
            ApplyAffinity(bounds);
            Marshal.StructureToPtr(new RECT { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height }, lParam, false);
            handled = true;
            return (IntPtr)1;
        }
        if (message == 0x007E /* WM_DISPLAYCHANGE */)
            Dispatcher.BeginInvoke((Action)PositionControls);
        return IntPtr.Zero;
    }

    /// <summary>Four thin strips just OUTSIDE the recorded pixels.</summary>
    private void ShowBorderStrips()
    {
        const int t = 3; // thickness
        int x = _region.X, y = _region.Y, w = _region.Width, h = _region.Height;
        MakeStrip(x - t, y - t, w + 2 * t, t);   // top
        MakeStrip(x - t, y + h, w + 2 * t, t);   // bottom
        MakeStrip(x - t, y, t, h);               // left
        MakeStrip(x + w, y, t, h);               // right
    }

    private void MakeStrip(int x, int y, int w, int h)
    {
        var strip = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            Left = -10000,
            Top = -10000,
            Width = 1,
            Height = 1,
        };
        strip.SourceInitialized += (_, _) =>
        {
            var sh = new WindowInteropHelper(strip).Handle;
            const int GWL_EXSTYLE = -20;
            const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLongPtr(sh, GWL_EXSTYLE, GetWindowLongPtr(sh, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ExcludeFromCapture(sh);
            MoveWindow(sh, x, y, w, h, true);
        };
        _strips.Add(strip);
        strip.Show();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseToggled?.Invoke(!_paused);
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        PauseText.Text = paused ? "Resume" : "Pause";
        PauseIcon.Data = (Geometry)FindResource(paused ? "Ico.Play" : "Ico.Pause");
        StateText.Text = paused ? "Paused" : "Recording";
        RecDot.Opacity = paused ? 0.35 : 1;
        System.Windows.Automation.AutomationProperties.SetName(PauseBtn, paused ? "Resume recording" : "Pause recording");
        foreach (var s in _strips)
            s.Opacity = _paused ? 0.35 : 1.0;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null; source = VisualTreeHelper.GetParent(source))
            if (source is System.Windows.Controls.Primitives.ButtonBase) return;
        try { DragMove(); } catch { }
    }

    /// <summary>WDA_EXCLUDEFROMCAPTURE (Win10 2004+).</summary>
    private static void ExcludeFromCapture(IntPtr hwnd) => SetWindowDisplayAffinity(hwnd, 0x11);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);
}
