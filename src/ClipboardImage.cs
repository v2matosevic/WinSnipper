using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSnipper;

/// <summary>
/// Puts an image on the clipboard as one CF_DIB whose pixels are prepared off
/// the UI thread. Windows derives CF_BITMAP and CF_DIBV5 from it on request.
///
/// WPF's Clipboard.SetImage does all of its work on the calling thread: a BMP
/// encode into a growing MemoryStream, a System.Drawing copy pushed through
/// BinaryFormatter, and a device bitmap, each the size of the image. For a
/// full-desktop snip that is hundreds of megabytes of short-lived large
/// objects and a frozen UI right after the selection.
/// </summary>
internal static class ClipboardImage
{
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int HeaderSize = 40; // BITMAPINFOHEADER

    /// <summary>Above this many pixels the payload is built on a worker.</summary>
    internal const long InlinePixels = 1_000_000;

    private static HwndSource? _owner;

    /// <summary>
    /// Builds the CF_DIB payload: header plus bottom-up 32-bit rows with opaque
    /// alpha, which every consumer reads the same way whether or not it honours
    /// the fourth byte. The caller owns the returned handle until
    /// <see cref="Place"/> succeeds. Safe on any thread for a frozen bitmap.
    /// </summary>
    internal static IntPtr BuildDib(BitmapSource image)
    {
        BitmapSource source = image;
        if (source.Format != PixelFormats.Bgr32 && source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
            converted.Freeze();
            source = converted;
        }

        int width = source.PixelWidth, height = source.PixelHeight;
        int stride = checked(width * 4);
        int pixelBytes = checked(stride * height);
        IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)(HeaderSize + (long)pixelBytes));
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr memory = GlobalLock(handle);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Marshal.WriteInt32(memory, 0, HeaderSize);
                Marshal.WriteInt32(memory, 4, width);
                Marshal.WriteInt32(memory, 8, height);   // positive: bottom-up, the form every reader accepts
                Marshal.WriteInt16(memory, 12, 1);       // planes
                Marshal.WriteInt16(memory, 14, 32);      // bits per pixel
                Marshal.WriteInt32(memory, 16, 0);       // BI_RGB
                Marshal.WriteInt32(memory, 20, pixelBytes);
                Marshal.WriteInt32(memory, 24, 3780);    // 96 DPI in pixels per metre
                Marshal.WriteInt32(memory, 28, 3780);
                Marshal.WriteInt32(memory, 32, 0);
                Marshal.WriteInt32(memory, 36, 0);

                IntPtr bits = memory + HeaderSize;
                source.CopyPixels(Int32Rect.Empty, bits, pixelBytes, stride); // top-down
                FlipRowsAndFillAlpha(bits, width, height);
            }
            finally { GlobalUnlock(handle); }
            return handle;
        }
        catch
        {
            GlobalFree(handle);
            throw;
        }
    }

    private static unsafe void FlipRowsAndFillAlpha(IntPtr bits, int width, int height)
    {
        int stride = width * 4;
        byte* top = (byte*)bits;
        byte* bottom = top + (long)stride * (height - 1);
        byte[] rented = ArrayPool<byte>.Shared.Rent(stride);
        try
        {
            fixed (byte* row = rented)
            {
                for (; top < bottom; top += stride, bottom -= stride)
                {
                    Buffer.MemoryCopy(top, row, stride, stride);
                    Buffer.MemoryCopy(bottom, top, stride, stride);
                    Buffer.MemoryCopy(row, bottom, stride, stride);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }

        uint* pixel = (uint*)bits;
        uint* end = pixel + (long)width * height;
        for (; pixel < end; pixel++) *pixel |= 0xFF000000;
    }

    /// <summary>
    /// Replaces the clipboard contents with the payload. On success Windows
    /// owns the handle; on failure (it throws) the caller still does. Call on
    /// the UI thread: the clipboard owner is a window of that thread.
    /// </summary>
    internal static void Place(IntPtr dib)
    {
        if (!OpenClipboard(Owner())) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (SetClipboardData(CF_DIB, dib) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { CloseClipboard(); }
    }

    internal static void Free(IntPtr dib)
    {
        if (dib != IntPtr.Zero) GlobalFree(dib);
    }

    // A message-only window: SetClipboardData is only reliable with an owner.
    private static IntPtr Owner()
    {
        if (_owner is null || _owner.IsDisposed)
        {
            _owner = new HwndSource(new HwndSourceParameters("WinSnipper.Clipboard")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            });
        }
        return _owner.Handle;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
