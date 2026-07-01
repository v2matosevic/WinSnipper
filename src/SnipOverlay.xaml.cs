using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSnipper;

/// <summary>
/// Fullscreen selection overlay across the whole virtual screen with three
/// pick modes: drag a region, click a window, or click a screen. For snips it
/// shows a frozen screenshot; for recordings it runs "live" (transparent over
/// the real desktop, so nothing appears to freeze). All selection math is in
/// physical screen pixels so results are pixel-exact regardless of DPI.
/// </summary>
public partial class SnipOverlay : Window
{
    private enum PickMode { Region, Window, Screen }

    private readonly Int32Rect _vs;
    private readonly bool _live;
    private PickMode _mode = PickMode.Region;
    private bool _dragging;
    private bool _done;
    private Point _startPx;
    private Point _curPx;

    // Hover target for Window/Screen modes, in physical screen px.
    private Int32Rect? _hover;
    private List<Int32Rect>? _windows; // top-level window rects, topmost first
    private List<Int32Rect>? _screens;

    /// <summary>Selected region relative to the virtual screen origin, in pixels.</summary>
    public Int32Rect? SelectionPx { get; private set; }

    /// <summary>True while any selection overlay is on screen — the snip and
    /// record hotkeys must not stack two fullscreen overlays.</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>Frozen-screenshot overlay (snips). Shows the shot, dims around the selection.</summary>
    public SnipOverlay(BitmapSource screenshot, Int32Rect virtualScreenPx) : this(virtualScreenPx, live: false)
    {
        ScreenImage.Source = screenshot;
    }

    /// <summary>Live overlay (recording): transparent over the real desktop, light dim.</summary>
    public SnipOverlay(Int32Rect virtualScreenPx) : this(virtualScreenPx, live: true) { }

    private SnipOverlay(Int32Rect virtualScreenPx, bool live)
    {
        if (live)
            AllowsTransparency = true; // must be set before the HWND exists
        InitializeComponent();
        if (live)
            Background = Brushes.Transparent; // after InitializeComponent — XAML sets Black
        IsOpen = true;
        Closed += (_, _) => IsOpen = false;
        _vs = virtualScreenPx;
        _live = live;
        if (live)
        {
            ScreenImage.Visibility = Visibility.Collapsed;
            // Alpha 1: visually nothing, but every pixel stays click-testable
            // (alpha 0 in a layered window would let clicks fall through).
            RootGrid.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            DimPath.Fill = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
        }
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            UpdateDim(null);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        MoveWindow(hwnd, _vs.X, _vs.Y, _vs.Width, _vs.Height, false);
    }

    // ---------- mode switching ----------

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (HintText is null) return; // initial IsChecked fires during InitializeComponent
        _mode = sender == ModeWindowBtn ? PickMode.Window
              : sender == ModeScreenBtn ? PickMode.Screen
              : PickMode.Region;
        HintText.Text = _mode switch
        {
            PickMode.Window => "Click a window   ·   Esc to cancel",
            PickMode.Screen => "Click a screen   ·   Esc to cancel",
            _ => "Drag to select   ·   Esc to cancel",
        };
        _dragging = false;
        _hover = null;
        if (_mode == PickMode.Window) _windows ??= EnumerateWindowRects();
        if (_mode == PickMode.Screen) _screens ??= EnumerateScreenRects();
        RefreshHover();
    }

    private void SetMode(PickMode mode)
    {
        (mode switch
        {
            PickMode.Window => ModeWindowBtn,
            PickMode.Screen => ModeScreenBtn,
            _ => ModeRegionBtn,
        }).IsChecked = true;
    }

    // ---------- mouse ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ModeBar.IsMouseOver) return;
        if (_mode != PickMode.Region) return;
        _dragging = true;
        _startPx = _curPx = CursorPx();
        CaptureMouse();
        UpdateRegionVisuals();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mode == PickMode.Region)
        {
            if (!_dragging) return;
            _curPx = CursorPx();
            UpdateRegionVisuals();
        }
        else
        {
            RefreshHover();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (ModeBar.IsMouseOver && !_dragging) return;

        if (_mode != PickMode.Region)
        {
            if (_hover is { } target)
                FinishWith(target.X - _vs.X, target.Y - _vs.Y, target.Width, target.Height);
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        _curPx = CursorPx();

        int x = (int)Math.Round(Math.Min(_startPx.X, _curPx.X)) - _vs.X;
        int y = (int)Math.Round(Math.Min(_startPx.Y, _curPx.Y)) - _vs.Y;
        int w = (int)Math.Round(Math.Abs(_curPx.X - _startPx.X));
        int h = (int)Math.Round(Math.Abs(_curPx.Y - _startPx.Y));
        FinishWith(x, y, w, h);
    }

    private void FinishWith(int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, _vs.Width);
        y = Math.Clamp(y, 0, _vs.Height);
        w = Math.Clamp(w, 0, _vs.Width - x);
        h = Math.Clamp(h, 0, _vs.Height - y);

        if (w < 4 || h < 4)
        {
            Finish(false);
            return;
        }

        SelectionPx = new Int32Rect(x, y, w, h);
        Finish(true);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Finish(false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Finish(false);
                break;
            case Key.D1 or Key.R:
                SetMode(PickMode.Region);
                break;
            case Key.D2 or Key.W:
                SetMode(PickMode.Window);
                break;
            case Key.D3 or Key.S:
                SetMode(PickMode.Screen);
                break;
        }
    }

    private void Finish(bool ok)
    {
        if (_done) return;
        _done = true;
        DialogResult = ok;
    }

    // ---------- hover pick (Window / Screen modes) ----------

    private void RefreshHover()
    {
        if (_mode == PickMode.Region) return;
        var p = CursorPx();
        var list = _mode == PickMode.Window ? _windows : _screens;
        Int32Rect? hit = null;
        if (list is not null)
        {
            foreach (var r in list) // topmost first
            {
                if (p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height)
                {
                    hit = r;
                    break;
                }
            }
        }
        _hover = hit;
        ShowHighlight(hit);
    }

    private void ShowHighlight(Int32Rect? rectPx)
    {
        if (rectPx is not { } r)
        {
            SelRect.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            UpdateDim(null);
            return;
        }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var a = ToDip(new Point(r.X, r.Y), scale);
        var b = ToDip(new Point(r.X + r.Width, r.Y + r.Height), scale);
        DrawSelection(new Rect(a, b), r.Width, r.Height);
    }

    private void UpdateRegionVisuals()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var a = ToDip(_startPx, scale);
        var b = ToDip(_curPx, scale);
        int pw = (int)Math.Round(Math.Abs(_curPx.X - _startPx.X));
        int ph = (int)Math.Round(Math.Abs(_curPx.Y - _startPx.Y));
        DrawSelection(new Rect(a, b), pw, ph);
    }

    private void DrawSelection(Rect r, int pxWidth, int pxHeight)
    {
        SelRect.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(SelRect, r.X);
        System.Windows.Controls.Canvas.SetTop(SelRect, r.Y);
        SelRect.Width = r.Width;
        SelRect.Height = r.Height;

        UpdateDim(r);

        SizeText.Text = $"{pxWidth} × {pxHeight}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = SizeBadge.DesiredSize.Width;
        double bh = SizeBadge.DesiredSize.Height;
        double bx = Math.Clamp(r.Right - bw, 4, Math.Max(4, ActualWidth - bw - 4));
        double by = r.Bottom + 8;
        if (by + bh > ActualHeight - 4) by = r.Bottom - bh - 8;
        System.Windows.Controls.Canvas.SetLeft(SizeBadge, bx);
        System.Windows.Controls.Canvas.SetTop(SizeBadge, by);
    }

    private void UpdateDim(Rect? selection)
    {
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        DimPath.Data = selection is { } r
            ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r))
            : full;
    }

    private Point ToDip(Point px, double scale) =>
        new((px.X - _vs.X) / scale, (px.Y - _vs.Y) / scale);

    private static Point CursorPx()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    // ---------- window / screen enumeration ----------

    private List<Int32Rect> EnumerateScreenRects()
    {
        var list = new List<Int32Rect>();
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
            list.Add(new Int32Rect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height));
        return list;
    }

    /// <summary>Visible top-level windows in z-order (topmost first), DWM frame bounds in px.</summary>
    private List<Int32Rect> EnumerateWindowRects()
    {
        var list = new List<Int32Rect>();
        int ownPid = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == ownPid) return true; // this overlay, thumbs, HUD

            long ex = GetWindowLongPtr(hwnd, -20 /* GWL_EXSTYLE */);
            if ((ex & 0x80) != 0) return true; // WS_EX_TOOLWINDOW

            // Cloaked windows (UWP ghosts, other virtual desktops) look
            // visible but aren't on screen.
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, 64);
            string c = cls.ToString();
            if (c is "Progman" or "WorkerW") return true; // wallpaper layers

            if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                if (!GetWindowRect(hwnd, out r)) return true;

            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 30 || h < 30) return true;
            list.Add(new Int32Rect(r.Left, r.Top, w, h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
}
