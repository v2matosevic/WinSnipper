using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinSnipper;

/// <summary>
/// Small always-on-top control pill shown while recording (elapsed time,
/// pause, stop). Both the pill and the companion region border are excluded
/// from screen capture, so they never appear in the video.
/// </summary>
public partial class RecordingHud : Window
{
    private readonly Int32Rect _region;
    private readonly DispatcherTimer _timer;
    private readonly Func<TimeSpan> _elapsed;
    private Window? _border;

    public event Action? StopRequested;
    public event Action<bool>? PauseToggled; // true = now paused

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
            _border?.Close();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        ExcludeFromCapture(hwnd);
        ShowRegionBorder();
        PositionNearRegion(hwnd);
    }

    private void PositionNearRegion(IntPtr hwnd)
    {
        // Measure the pill in DIPs, convert to px for MoveWindow.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int w = (int)Math.Ceiling(DesiredSize.Width * scale);
        int h = (int)Math.Ceiling(DesiredSize.Height * scale);

        int vsL = GetSystemMetrics(76), vsT = GetSystemMetrics(77);
        int vsW = GetSystemMetrics(78), vsH = GetSystemMetrics(79);

        int x = Math.Clamp(_region.X + _region.Width - w, vsL + 8, vsL + vsW - w - 8);
        int y = _region.Y + _region.Height + 10;                 // prefer below the region
        if (y + h > vsT + vsH - 8) y = _region.Y - h - 10;       // else above
        if (y < vsT + 8) y = _region.Y + _region.Height - h - 10; // else inside, bottom edge

        MoveWindow(hwnd, x, y, w, h, true);
    }

    /// <summary>Click-through outline around the recorded region.</summary>
    private void ShowRegionBorder()
    {
        const int pad = 2; // sits just outside the captured pixels
        _border = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false,
            Left = -10000,
            Top = -10000,
            Content = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
                StrokeThickness = 2,
                RadiusX = 2,
                RadiusY = 2,
            },
        };
        _border.SourceInitialized += (_, _) =>
        {
            var bh = new WindowInteropHelper(_border).Handle;
            const int GWL_EXSTYLE = -20;
            const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLongPtr(bh, GWL_EXSTYLE, GetWindowLongPtr(bh, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ExcludeFromCapture(bh);
            MoveWindow(bh, _region.X - pad, _region.Y - pad, _region.Width + 2 * pad, _region.Height + 2 * pad, true);
        };
        _border.Show();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseBtn.Content = _paused ? "▶" : "⏸";
        PauseBtn.ToolTip = _paused ? "Resume" : "Pause";
        if (_border is not null)
            _border.Opacity = _paused ? 0.35 : 1.0;
        PauseToggled?.Invoke(_paused);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Button)
            try { DragMove(); } catch { }
    }

    /// <summary>WDA_EXCLUDEFROMCAPTURE (Win10 2004+); older builds just show the window in the video.</summary>
    private static void ExcludeFromCapture(IntPtr hwnd) => SetWindowDisplayAffinity(hwnd, 0x11);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);
}
