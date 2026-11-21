using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSnipper.Recording;

/// <summary>
/// The recorder's working frame: top-down 32-bit pixels in a DIB section with
/// its own device context. The screen (GDI fallback) and the cursor are drawn
/// into it by GDI directly, and the encoder copy reads the same memory.
/// The previous System.Drawing frame paid a full-frame copy in and out of a
/// temporary bitmap on every cursor draw.
/// </summary>
internal sealed class FrameSurface : IDisposable
{
    private const int SRCCOPY = 0x00CC0020;
    private IntPtr _dc, _dib, _previous;

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public IntPtr Bits { get; }

    public FrameSurface(int width, int height)
    {
        Width = width;
        Height = height;
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down, the layout the encoder is given
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(IntPtr.Zero, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Bits = bits;
        _dc = CreateCompatibleDC(IntPtr.Zero);
        if (_dc == IntPtr.Zero)
        {
            DeleteObject(_dib);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        _previous = SelectObject(_dc, _dib);
    }

    /// <summary>Takes the pixels of a capture backend's accumulated frame.</summary>
    public void CopyFrom(Bitmap source)
    {
        var data = source.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            if (data.Stride == Stride)
                CopyMemory(Bits, data.Scan0, (UIntPtr)((long)Stride * Height));
            else
                for (int row = 0; row < Height; row++)
                    CopyMemory(Bits + row * Stride, data.Scan0 + row * data.Stride, (UIntPtr)Stride);
        }
        finally { source.UnlockBits(data); }
    }

    /// <summary>GDI capture of a screen region (the compatibility backend).</summary>
    public void CopyScreen(int x, int y)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        try { BitBlt(_dc, 0, 0, Width, Height, screen, x, y, SRCCOPY); }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        GdiFlush();
    }

    /// <summary>Draws the pointer where it was when <paramref name="cursor"/> was read.</summary>
    public void DrawCursor(CursorState cursor, int regionX, int regionY)
    {
        if (!cursor.Visible) return;
        int x = cursor.X - regionX, y = cursor.Y - regionY;
        if (GetIconInfo(cursor.Handle, out var icon))
        {
            x -= icon.xHotspot;
            y -= icon.yHotspot;
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
        }
        DrawIconEx(_dc, x, y, cursor.Handle, 0, 0, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
        GdiFlush(); // GDI batches; the pixels are read right after this
    }

    /// <summary>An owned, frozen copy (the thumbnail's last frame).</summary>
    public BitmapSource ToBitmapSource()
    {
        var copy = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Bits, Stride * Height, Stride);
        copy.Freeze();
        return copy;
    }

    public void Dispose()
    {
        if (_dc != IntPtr.Zero)
        {
            SelectObject(_dc, _previous);
            DeleteDC(_dc);
            _dc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero)
        {
            DeleteObject(_dib);
            _dib = IntPtr.Zero;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hDC, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr flicker, int flags);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] private static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);
}

/// <summary>Where the pointer is and what it looks like; equal states draw the same pixels.</summary>
internal readonly record struct CursorState(bool Visible, IntPtr Handle, int X, int Y)
{
    public static CursorState Read()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref info) && info.flags == 1 /* CURSOR_SHOWING */
            ? new CursorState(true, info.hCursor, info.ptScreenPos.X, info.ptScreenPos.Y)
            : default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO info);
}
