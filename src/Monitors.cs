using System.Runtime.InteropServices;

namespace WinSnipper;

/// <summary>
/// Per-monitor geometry in physical pixels.
///
/// WPF only exposes <c>SystemParameters.WorkArea</c>, which is the *primary*
/// monitor — useless for placing a window on the display the user is actually
/// looking at. Everything here is raw Win32 so the numbers stay in physical
/// pixels, and <see cref="Work.Scale"/> converts WPF's DIPs to them for the
/// target monitor (which may run a different DPI than the window's current one).
/// </summary>
internal static class Monitors
{
    /// <summary>Work area (screen minus taskbar) of one monitor, in physical pixels.</summary>
    internal readonly record struct Work(IntPtr Handle, int Left, int Top, int Right, int Bottom, double Scale)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>Monitor containing the point (nearest one if it falls in a gap).</summary>
    public static Work FromPoint(int x, int y) =>
        Describe(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST));

    /// <summary>Monitor containing the centre of a virtual-screen rectangle.</summary>
    public static Work FromRect(int x, int y, int width, int height) =>
        FromPoint(x + width / 2, y + height / 2);

    /// <summary>Monitor under the mouse cursor — the fallback "screen I'm using".</summary>
    public static Work FromCursor() =>
        GetCursorPos(out var p) ? FromPoint(p.X, p.Y) : Primary();

    /// <summary>Monitor of the window that currently has focus, or null if it can't be resolved.</summary>
    public static Work? FromForegroundWindow()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL);
        return mon == IntPtr.Zero ? null : Describe(mon);
    }

    public static Work Primary() => FromPoint(0, 0);

    private static Work Describe(IntPtr hMonitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMonitor == IntPtr.Zero || !GetMonitorInfo(hMonitor, ref mi))
            return new Work(IntPtr.Zero, 0, 0, 1920, 1080, 1.0);

        return new Work(hMonitor, mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom,
                        ScaleOf(hMonitor));
    }

    /// <summary>DIP → physical pixel factor for this monitor (1.5 at 150%).</summary>
    private static double ScaleOf(IntPtr hMonitor)
    {
        try
        {
            if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch
        {
            // shcore.dll is Windows 8.1+; the app targets 10/11, but never let
            // a DPI probe take down a screenshot.
        }
        return 1.0;
    }

    // ---------- interop ----------

    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>Window's on-screen rectangle in physical pixels, or null if it has no handle yet.</summary>
    public static (int Left, int Top)? TopLeftOf(System.Windows.Window window)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        return (r.Left, r.Top);
    }

    /// <summary>
    /// Moves a window to an exact physical-pixel rectangle. Going through
    /// SetWindowPos rather than Window.Left/Top sidesteps WPF's DIP conversion,
    /// which uses the *source* monitor's DPI and lands the window in the wrong
    /// place whenever the two displays scale differently.
    /// </summary>
    public static void MoveTo(System.Windows.Window window, int x, int y, int widthPx, int heightPx)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, widthPx, heightPx, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
