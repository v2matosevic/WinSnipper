using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace WinSnipper;

public static class ScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int SRCCOPY = 0x00CC0020;
    private const uint PAGE_READWRITE = 0x04;
    private const uint DIB_RGB_COLORS = 0;

    /// <summary>
    /// Keeps each capture's file-mapping handle alive exactly as long as the
    /// bitmap that reads through it, and no longer.
    /// </summary>
    private static readonly ConditionalWeakTable<BitmapSource, SectionHandle> Sections = new();

    /// <summary>Bounds of the virtual screen (all monitors) in physical pixels.</summary>
    public static Int32Rect VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>
    /// Captures the entire virtual screen (all monitors) in physical pixels.
    /// Returns the frozen image plus the virtual-screen bounds in screen pixel
    /// coordinates.
    ///
    /// The desktop is blitted straight into a shared memory section that WPF
    /// then reads in place, so a capture costs one blit and nothing else. The
    /// obvious version — blit into a GDI bitmap, then hand the pixels to
    /// BitmapSource.Create — copies the whole desktop a second time into a
    /// fresh large-object-heap array on every single snip. At 5760 × 1080 that
    /// is roughly 24 MB of garbage per capture, which is exactly the cost you
    /// cannot afford on a machine that is already short of memory.
    /// </summary>
    public static (BitmapSource image, Int32Rect boundsPx) CaptureVirtualScreen()
    {
        var vs = VirtualScreenBounds();
        int width = vs.Width;
        int height = vs.Height;
        int stride = width * 4; // 32bpp rows are always 4-byte aligned
        long bytes = (long)stride * height;

        var section = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE,
            (uint)(bytes >> 32), (uint)(bytes & 0xFFFFFFFF), IntPtr.Zero);
        if (section.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            BlitDesktopInto(section, vs, width, height);

            var source = Imaging.CreateBitmapSourceFromMemorySection(
                section.DangerousGetHandle(), width, height, PixelFormats.Bgr32, stride, 0);

            // Everything downstream (cropping, the background PNG write, the
            // editor) assumes a frozen, thread-safe image. If this ever stops
            // freezing, fall back to owning the pixels outright rather than
            // handing out something that cannot leave the UI thread.
            if (!source.CanFreeze)
                return (CopyOut(source, width, height, stride), new Int32Rect(vs.X, vs.Y, width, height));

            source.Freeze();
            section.Account(bytes);
            Sections.Add(source, section);
            return (source, new Int32Rect(vs.X, vs.Y, width, height));
        }
        catch
        {
            section.Dispose();
            throw;
        }
    }

    private static void BlitDesktopInto(SectionHandle section, Int32Rect vs, int width, int height)
    {
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // negative height = top-down rows, which is WPF's layout
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            dib = CreateDIBSection(IntPtr.Zero, ref info, DIB_RGB_COLORS, out _,
                section.DangerousGetHandle(), 0);
            if (dib == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            previous = SelectObject(memDc, dib);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, vs.X, vs.Y, SRCCOPY))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // GDI batches drawing; without this the section can still be empty
            // when WPF reads it.
            GdiFlush();
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memDc, previous);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            if (dib != IntPtr.Zero) DeleteObject(dib);
        }
    }

    private static BitmapSource CopyOut(BitmapSource source, int width, int height, int stride)
    {
        var buffer = new byte[(long)stride * height];
        source.CopyPixels(buffer, stride, 0);
        var owned = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, buffer, stride);
        owned.Freeze();
        return owned;
    }

    /// <summary>
    /// A capture's backing memory is unmanaged, so the GC has no idea how much
    /// it is holding on to. Accounting for it keeps a run of snips from piling
    /// up tens of megabytes apiece before a collection notices.
    /// </summary>
    private sealed class SectionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private long _pressure;

        public SectionHandle() : base(ownsHandle: true) { }

        public void Account(long bytes)
        {
            _pressure = bytes;
            GC.AddMemoryPressure(bytes);
        }

        protected override bool ReleaseHandle()
        {
            bool closed = CloseHandle(handle);
            if (_pressure > 0)
            {
                GC.RemoveMemoryPressure(_pressure);
                _pressure = 0;
            }
            return closed;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hDC, ref BITMAPINFOHEADER header,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hDest, int x, int y, int width, int height,
        IntPtr hSrc, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SectionHandle CreateFileMapping(IntPtr file, IntPtr attributes,
        uint protect, uint maxSizeHigh, uint maxSizeLow, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
