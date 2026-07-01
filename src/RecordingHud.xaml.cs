using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinSnipper;

/// <summary>
/// Small always-on-top control pill shown while recording (elapsed time,
/// pause, stop), plus four thin border strips around the recorded region.
/// Everything is excluded from capture — and, because GDI capture renders
/// excluded windows as BLACK, nothing here may ever overlap the recorded
/// pixels: the strips sit outside the region, and if the pill can't fit
/// outside it stays hidden (the hotkey still stops the recording).
/// </summary>
public partial class RecordingHud : Window
{
    private readonly Int32Rect _region;
    private readonly DispatcherTimer _timer;
    private readonly Func<TimeSpan> _elapsed;
    private readonly List<Window> _strips = new();

    public event Action? StopRequested;
    public event Action<bool>? PauseToggled; // true = now paused

    /// <summary>True when there was no room for the pill outside the recorded
    /// region — the caller should tell the user the hotkey stops the recording.</summary>
    public bool PillHidden { get; private set; }

    private bool _paused;

    public RecordingHud(Int32Rect regionPx, Func<TimeSpan> elapsed)
    {
        InitializeComponent();
        _region = regionPx;
        _elapsed = elapsed;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            var t = _elapsed();
            TimeText.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        };
        _timer.Start();

        var blink = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        RecDot.BeginAnimation(OpacityProperty, blink);

        Closed += (_, _) =>
        {
            _timer.Stop();
            foreach (var s in _strips) s.Close();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        ExcludeFromCapture(hwnd);
        ShowBorderStrips();
        PositionOutsideRegion(hwnd);
    }

    private void PositionOutsideRegion(IntPtr hwnd)
    {
        // Measure the pill in DIPs, convert to px for MoveWindow.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int w = (int)Math.Ceiling(DesiredSize.Width * scale);
        int h = (int)Math.Ceiling(DesiredSize.Height * scale);

        var vs = ScreenCapture.VirtualScreenBounds();
        int x = Math.Clamp(_region.X + _region.Width - w, vs.X + 8, vs.X + vs.Width - w - 8);

        int y;
        if (_region.Y + _region.Height + 10 + h <= vs.Y + vs.Height - 8)
            y = _region.Y + _region.Height + 10;              // below the region
        else if (_region.Y - h - 10 >= vs.Y + 8)
            y = _region.Y - h - 10;                           // above it
        else if (_region.X - w - 10 >= vs.X + 8)
        {                                                     // left of it
            x = _region.X - w - 10;
            y = Math.Clamp(_region.Y + _region.Height - h, vs.Y + 8, vs.Y + vs.Height - h - 8);
        }
        else if (_region.X + _region.Width + 10 + w <= vs.X + vs.Width - 8)
        {                                                     // right of it
            x = _region.X + _region.Width + 10;
            y = Math.Clamp(_region.Y + _region.Height - h, vs.Y + 8, vs.Y + vs.Height - h - 8);
        }
        else
        {
            // Region fills the whole virtual screen — an excluded pill would
            // record as a black box, so keep it off screen entirely.
            PillHidden = true;
            MoveWindow(hwnd, vs.X - w - 100, vs.Y, w, h, false);
            return;
        }

        MoveWindow(hwnd, x, y, w, h, true);
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
        _paused = !_paused;
        PauseBtn.Content = _paused ? "▶" : "⏸";
        PauseBtn.ToolTip = _paused ? "Resume" : "Pause";
        foreach (var s in _strips)
            s.Opacity = _paused ? 0.35 : 1.0;
        PauseToggled?.Invoke(_paused);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Button)
            try { DragMove(); } catch { }
    }

    /// <summary>WDA_EXCLUDEFROMCAPTURE (Win10 2004+).</summary>
    private static void ExcludeFromCapture(IntPtr hwnd) => SetWindowDisplayAffinity(hwnd, 0x11);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);
}
