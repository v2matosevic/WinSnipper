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

    // The interactive selectors reuse one section and one WPF bitmap for the
    // whole session. WPF answers every new desktop-sized bitmap with a full
    // garbage collection, and on a machine short of memory that collection
    // drags the whole heap back from disk at the moment of the hotkey
    // (docs/PERFORMANCE-2026-10-29.md). Re-blitting and invalidating the same
    // bitmap costs no collection at all.
    private static readonly object SharedGate = new();
    private static SharedFrame? _sharedTarget; // worker side: where the next blit goes
    private static SharedFrame? _sharedShown;  // UI side: what _sharedBitmap reads
    private static InteropBitmap? _sharedBitmap;

    /// <summary>The shared section and the desktop bounds its last blit covered.</summary>
    internal sealed class SharedFrame(SectionHandle section, Int32Rect bounds)
    {
        internal SectionHandle Section { get; } = section;
        internal Int32Rect Bounds { get; } = bounds;
    }

    /// <summary>
    /// Worker side of an interactive capture: blits the virtual screen into
    /// the session's shared section, replacing the section only when the
    /// desktop size changes. Never call while a selector shows the section.
    /// </summary>
    internal static SharedFrame BlitShared()
    {
        lock (SharedGate)
        {
            var vs = VirtualScreenBounds();
            var target = _sharedTarget;
            if (target is null || target.Bounds.Width != vs.Width || target.Bounds.Height != vs.Height)
            {
                long bytes = (long)vs.Width * 4 * vs.Height;
                var section = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE,
                    (uint)(bytes >> 32), (uint)(bytes & 0xFFFFFFFF), IntPtr.Zero);
                if (section.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                // A replaced section stays alive while a bitmap still reads it.
                target = new SharedFrame(section, vs);
            }
            if (_keepResident) target.Section.KeepResident((long)vs.Width * 4 * vs.Height);
            // Not locked (quota refused): at least tell Windows the old pixels
            // are worthless, so a paged-out surface is not read back from disk
            // only to be overwritten.
            target.Section.DiscardContents();
            BlitDesktopInto(target.Section, vs, vs.Width, vs.Height);
            _sharedTarget = new SharedFrame(target.Section, vs);
            return _sharedTarget;
        }
    }

    private static bool _keepResident;

    /// <summary>
    /// Locks the session capture surface into physical memory, now and for any
    /// surface that replaces it. The surface is a desktop-sized buffer (24 MB
    /// at 5760 x 1080) that is touched only at the hotkey; left alone it is
    /// the first thing Windows pages out of an idle app, and the next screen
    /// copy then waits for thousands of page reads before it can overwrite
    /// them (the 0.7 to 3.9 s copies after idle in November's session.log). A
    /// locked range also survives EmptyWorkingSet, which the hard minimum
    /// working set does not. Call after <see cref="Residency.Pin"/>: the lock
    /// counts against the minimum working set. Returns a session-log line.
    /// </summary>
    internal static string KeepSharedResident()
    {
        lock (SharedGate)
        {
            _keepResident = true;
            if (_sharedTarget is not { } target) return "capture-surface not created yet";
            long bytes = (long)target.Bounds.Width * 4 * target.Bounds.Height;
            return target.Section.KeepResident(bytes)
                ? $"capture-surface locked={bytes >> 20}MB"
                : $"capture-surface lock-unavailable error={target.Section.LockError}";
        }
    }

    /// <summary>True when the current capture surface is locked in memory (tests and diagnostics).</summary>
    internal static bool SharedIsResident
    {
        get { lock (SharedGate) return _sharedTarget?.Section.Locked == true; }
    }

    /// <summary>The mapped address of the current capture surface, or zero (tests).</summary>
    internal static IntPtr SharedView
    {
        get { lock (SharedGate) return _sharedTarget?.Section.View ?? IntPtr.Zero; }
    }

    /// <summary>
    /// UI side: the session bitmap over the frame's section, invalidated so
    /// WPF reads the new pixels. Unfrozen and owned by the calling (UI)
    /// thread; anything that outlives the selector takes <see cref="CopyRegion"/>.
    ///
    /// Invalidate() only reaches the render thread while some visual holds
    /// the bitmap. One that nothing holds keeps its GPU copy of an earlier
    /// capture and shows that when it is attached again, so the selectors
    /// keep it attached while hidden and retire it when they close.
    /// </summary>
    internal static BitmapSource PresentShared(SharedFrame frame)
    {
        if (_sharedBitmap is not null && ReferenceEquals(_sharedShown?.Section, frame.Section))
        {
            _sharedBitmap.Invalidate();
        }
        else
        {
            var b = frame.Bounds;
            var bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(
                frame.Section.DangerousGetHandle(), b.Width, b.Height, PixelFormats.Bgr32, b.Width * 4, 0);
            Sections.AddOrUpdate(bitmap, frame.Section);
            _sharedBitmap = bitmap;
        }
        _sharedShown = frame;
        return _sharedBitmap;
    }

    /// <summary>
    /// A selector that may hold the session bitmap is closing. If it was the
    /// last holder, the render thread no longer hears invalidations, so the
    /// next capture builds a new bitmap over the same section (one full
    /// collection, only after a selector window is destroyed).
    /// </summary>
    internal static void RetireShared(ImageSource? bitmap)
    {
        if (bitmap is not null && ReferenceEquals(bitmap, _sharedBitmap))
            _sharedBitmap = null;
    }

    /// <summary>
    /// An owned, frozen copy of one region. The selector's pixels are
    /// overwritten by the next capture, so the thumbnail, editor, clipboard
    /// and PNG all work from this copy instead.
    /// </summary>
    internal static BitmapSource CopyRegion(BitmapSource source, Int32Rect region)
    {
        int stride = region.Width * ((source.Format.BitsPerPixel + 7) / 8);
        int size = checked(stride * region.Height);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            source.CopyPixels(region, buffer, size, stride);
            var copy = BitmapSource.Create(region.Width, region.Height, source.DpiX, source.DpiY,
                source.Format, source.Palette, buffer, size, stride);
            copy.Freeze();
            return copy;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Bounds of the virtual screen (all monitors) in physical pixels.</summary>
    public static Int32Rect VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>
    /// Captures the entire virtual screen (all monitors) in physical pixels.
    /// Returns the frozen image plus the virtual-screen bounds in screen pixel
    /// coordinates. Standalone captures (self-test, benchmarks) only: each one
    /// is a new desktop-sized bitmap, which costs a full collection. The
    /// selectors use <see cref="BlitShared"/> and <see cref="PresentShared"/>.
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
    internal sealed class SectionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private long _pressure;
        private long _viewBytes;

        public SectionHandle() : base(ownsHandle: true) { }

        internal IntPtr View { get; private set; }
        internal bool Locked { get; private set; }
        internal int LockError { get; private set; }

        /// <summary>Maps the section once and locks that view into physical memory.</summary>
        internal bool KeepResident(long bytes)
        {
            if (Locked) return true;
            if (View == IntPtr.Zero)
            {
                View = MapViewOfFile(handle, FILE_MAP_WRITE, 0, 0, UIntPtr.Zero);
                if (View == IntPtr.Zero) { LockError = Marshal.GetLastWin32Error(); return false; }
                _viewBytes = bytes;
            }
            Locked = VirtualLock(View, (nuint)bytes);
            LockError = Locked ? 0 : Marshal.GetLastWin32Error();
            return Locked;
        }

        /// <summary>Marks the mapped pixels as not worth reading back from the page file. No-op when locked or unmapped.</summary>
        internal void DiscardContents()
        {
            if (Locked || View == IntPtr.Zero) return;
            VirtualAlloc(View, (nuint)_viewBytes, MEM_RESET, PAGE_READWRITE);
        }

        public void Account(long bytes)
        {
            _pressure = bytes;
            GC.AddMemoryPressure(bytes);
        }

        protected override bool ReleaseHandle()
        {
            if (View != IntPtr.Zero)
            {
                UnmapViewOfFile(View); // also releases the lock
                View = IntPtr.Zero;
                Locked = false;
            }
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

    private const uint FILE_MAP_WRITE = 0x0002;
    private const uint MEM_RESET = 0x00080000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr section, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr address);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualLock(IntPtr address, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint bytes, uint type, uint protect);
}
