using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace WinSnipper;

public static class ScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>Bounds of the virtual screen (all monitors) in physical pixels.</summary>
    public static Int32Rect VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>
    /// Captures the entire virtual screen (all monitors) in physical pixels.
    /// Returns the frozen image plus the virtual-screen bounds in screen pixel coordinates.
    /// </summary>
    public static (BitmapSource image, Int32Rect boundsPx) CaptureVirtualScreen()
    {
        var vs = VirtualScreenBounds();
        int left = vs.X;
        int top = vs.Y;
        int width = vs.Width;
        int height = vs.Height;

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);

        // Copy the opaque desktop pixels directly into WPF. GetHbitmap adds
        // another full-screen allocation and an unnecessary alpha conversion.
        var data = bmp.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var source = BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgr32, null, data.Scan0, checked(data.Stride * height), data.Stride);
            source.Freeze();
            return (source, new Int32Rect(left, top, width, height));
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
