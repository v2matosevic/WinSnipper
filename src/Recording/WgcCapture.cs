#if WINRT
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WinSnipper.Recording;

/// <summary>
/// Captures a screen region via Windows.Graphics.Capture (WinRT flavor only).
/// This is the only capture API that reliably includes hardware-overlay (MPO)
/// planes — where browsers put playing video — which both GDI BitBlt and
/// DXGI Desktop Duplication render black on modern Windows 11 drivers.
/// Throws from the constructor when unavailable; callers fall back to
/// Desktop Duplication, then GDI.
/// </summary>
internal sealed class WgcCapture : IRegionCapture
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly ID3D11Texture2D _staging;

    private readonly int _w, _h;
    private readonly int _srcX, _srcY; // region offset inside the captured monitor

    public WgcCapture(System.Windows.Int32Rect regionPx)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows.Graphics.Capture not supported");

        // The capture item is per-monitor; the region must fit one monitor.
        var center = new POINT
        {
            X = regionPx.X + regionPx.Width / 2,
            Y = regionPx.Y + regionPx.Height / 2,
        };
        IntPtr hmon = MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref mi))
            throw new NotSupportedException("monitor lookup failed");
        var mon = mi.rcMonitor;
        if (regionPx.X < mon.Left || regionPx.Y < mon.Top
            || regionPx.X + regionPx.Width > mon.Right
            || regionPx.Y + regionPx.Height > mon.Bottom)
            throw new NotSupportedException("region is not contained in a single monitor");

        _w = regionPx.Width;
        _h = regionPx.Height;
        _srcX = regionPx.X - mon.Left;
        _srcY = regionPx.Y - mon.Top;

        int hr = D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero,
            0x20 /* D3D11_CREATE_DEVICE_BGRA_SUPPORT */,
            IntPtr.Zero, 0, 7 /* D3D11_SDK_VERSION */, out _device, out _, out _ctx);
        Marshal.ThrowExceptionForHR(hr);

        // ID3D11Device → IDXGIDevice → WinRT IDirect3DDevice.
        IntPtr unk = Marshal.GetIUnknownForObject(_device);
        var iidDxgi = typeof(IDXGIDevice).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iidDxgi, out IntPtr dxgiPtr));
        Marshal.Release(unk);
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out IntPtr inspectable));
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }
        finally
        {
            Marshal.Release(dxgiPtr);
        }

        var item = CreateItemForMonitor(hmon);
        int monW = mon.Right - mon.Left, monH = mon.Bottom - mon.Top;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
            new Windows.Graphics.SizeInt32 { Width = monW, Height = monH });
        _session = _framePool.CreateCaptureSession(item);
        try { _session.IsCursorCaptureEnabled = false; } catch { } // we draw it ourselves
        try { _session.IsBorderRequired = false; } catch { }       // 22621+; else a system border shows

        var texDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_w,
            Height = (uint)_h,
            MipLevels = 1,
            ArraySize = 1,
            Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
            SampleCount = 1,
            SampleQuality = 0,
            Usage = 3,          // D3D11_USAGE_STAGING
            BindFlags = 0,
            CPUAccessFlags = 0x20000, // D3D11_CPU_ACCESS_READ
            MiscFlags = 0,
        };
        Marshal.ThrowExceptionForHR(_device.CreateTexture2D(ref texDesc, IntPtr.Zero, out _staging));

        _session.StartCapture();
    }

    public bool TryAccumulateInto(Bitmap bmp)
    {
        using var frame = _framePool.TryGetNextFrame();
        if (frame is null)
            return false;

        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var iidTex = typeof(ID3D11Texture2D).GUID;
        IntPtr texPtr = access.GetInterface(ref iidTex);
        var tex = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texPtr);
        Marshal.Release(texPtr);
        try
        {
            var box = new D3D11_BOX
            {
                Left = (uint)_srcX,
                Top = (uint)_srcY,
                Front = 0,
                Right = (uint)(_srcX + _w),
                Bottom = (uint)(_srcY + _h),
                Back = 1,
            };
            _ctx.CopySubresourceRegion(_staging, 0, 0, 0, 0, tex, 0, ref box);
        }
        finally
        {
            if (Marshal.IsComObject(tex)) Marshal.ReleaseComObject(tex);
        }

        Marshal.ThrowExceptionForHR(_ctx.Map(_staging, 0, 1 /* D3D11_MAP_READ */, 0, out var mapped));
        try
        {
            var data = bmp.LockBits(new Rectangle(0, 0, _w, _h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < _h; row++)
                    CopyMemory(data.Scan0 + row * data.Stride,
                        mapped.pData + row * (int)mapped.RowPitch, (UIntPtr)(_w * 4));
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        finally
        {
            _ctx.Unmap(_staging, 0);
        }
        return true;
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { }
        try { _framePool.Dispose(); } catch { }
        try { _winrtDevice.Dispose(); } catch { }
        if (Marshal.IsComObject(_staging)) Marshal.ReleaseComObject(_staging);
        if (Marshal.IsComObject(_ctx)) Marshal.ReleaseComObject(_ctx);
        if (Marshal.IsComObject(_device)) Marshal.ReleaseComObject(_device);
    }

    // ---------- WinRT interop ----------

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out IntPtr hstring));
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iidInterop = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iidInterop, out factory));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            var iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            IntPtr abi = interop.CreateForMonitor(hmon, ref iidItem);
            var item = GraphicsCaptureItem.FromAbi(abi);
            Marshal.Release(abi);
            if (Marshal.IsComObject(interop)) Marshal.ReleaseComObject(interop);
            return item;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            WindowsDeleteString(hstring);
        }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

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
    private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO mi);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out ID3D11Device device, out int featureLevel, out ID3D11DeviceContext context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);
}
#endif
