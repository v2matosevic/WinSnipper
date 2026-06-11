using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSnipper;

/// <summary>
/// Fullscreen frozen-frame overlay across the whole virtual screen.
/// All selection math is done in physical screen pixels (via GetCursorPos)
/// so the crop is pixel-exact regardless of DPI scaling.
/// </summary>
public partial class SnipOverlay : Window
{
    private readonly Int32Rect _vs;
    private bool _dragging;
    private bool _done;
    private Point _startPx;
    private Point _curPx;

    /// <summary>Selected region relative to the captured bitmap, in pixels.</summary>
    public Int32Rect? SelectionPx { get; private set; }

    public SnipOverlay(BitmapSource screenshot, Int32Rect virtualScreenPx)
    {
        InitializeComponent();
        _vs = virtualScreenPx;
        ScreenImage.Source = screenshot;
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _startPx = _curPx = CursorPx();
        HintBadge.Visibility = Visibility.Collapsed;
        CaptureMouse();
        UpdateVisuals();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _curPx = CursorPx();
        UpdateVisuals();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        _curPx = CursorPx();

        int x = (int)Math.Round(Math.Min(_startPx.X, _curPx.X)) - _vs.X;
        int y = (int)Math.Round(Math.Min(_startPx.Y, _curPx.Y)) - _vs.Y;
        int w = (int)Math.Round(Math.Abs(_curPx.X - _startPx.X));
        int h = (int)Math.Round(Math.Abs(_curPx.Y - _startPx.Y));

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
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(false);
        }
    }

    private void Finish(bool ok)
    {
        if (_done) return;
        _done = true;
        DialogResult = ok;
    }

    private void UpdateVisuals()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var a = ToDip(_startPx, scale);
        var b = ToDip(_curPx, scale);
        var r = new Rect(a, b);

        SelRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelRect, r.X);
        Canvas.SetTop(SelRect, r.Y);
        SelRect.Width = r.Width;
        SelRect.Height = r.Height;

        UpdateDim(r);

        int pw = (int)Math.Round(Math.Abs(_curPx.X - _startPx.X));
        int ph = (int)Math.Round(Math.Abs(_curPx.Y - _startPx.Y));
        SizeText.Text = $"{pw} × {ph}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = SizeBadge.DesiredSize.Width;
        double bh = SizeBadge.DesiredSize.Height;
        double bx = Math.Clamp(r.Right - bw, 4, Math.Max(4, ActualWidth - bw - 4));
        double by = r.Bottom + 8;
        if (by + bh > ActualHeight - 4) by = r.Bottom - bh - 8;
        Canvas.SetLeft(SizeBadge, bx);
        Canvas.SetTop(SizeBadge, by);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);
}
