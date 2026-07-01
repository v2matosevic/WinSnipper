using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;

namespace WinSnipper.Recording;

/// <summary>
/// Captures a screen region via DXGI Desktop Duplication. Unlike GDI BitBlt,
/// this sees the final composed desktop — including hardware-overlay video
/// (browsers put playing video on MPO planes, which BitBlt renders black).
/// Throws from the constructor when duplication isn't possible (region spans
/// monitors, rotated display, RDP, unsupported GPU) — callers fall back to GDI.
/// </summary>
internal sealed class DesktopDuplicator : IDisposable
{
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly IDXGIOutput1 _output;
    private readonly ID3D11Texture2D _staging;
    private IDXGIOutputDuplication? _dup;

    private readonly int _w, _h;
    private readonly int _srcX, _srcY; // region offset inside the duplicated output

    public DesktopDuplicator(Int32Rect regionPx)
    {
        int hr = D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0,
            IntPtr.Zero, 0, 7 /* D3D11_SDK_VERSION */, out _device, out _, out _ctx);
        Marshal.ThrowExceptionForHR(hr);

        var dxgiDevice = (IDXGIDevice)_device;
        Marshal.ThrowExceptionForHR(dxgiDevice.GetAdapter(out var adapter));

        // Find the output whose desktop rect fully contains the region.
        IDXGIOutput1? match = null;
        DXGI_OUTPUT_DESC desc = default;
        for (uint i = 0; adapter.EnumOutputs(i, out var output) == 0; i++)
        {
            output.GetDesc(out var d);
            var r = d.DesktopCoordinates;
            if (regionPx.X >= r.Left && regionPx.Y >= r.Top
                && regionPx.X + regionPx.Width <= r.Right
                && regionPx.Y + regionPx.Height <= r.Bottom)
            {
                match = (IDXGIOutput1)output;
                desc = d;
                break;
            }
            Release(output);
        }
        Release(adapter);
        if (match is null)
            throw new NotSupportedException("region is not contained in a single output");
        if (desc.Rotation is not (0 /* UNSPECIFIED */ or 1 /* IDENTITY */))
        {
            Release(match);
            throw new NotSupportedException("rotated displays are not supported");
        }

        _output = match;
        _w = regionPx.Width;
        _h = regionPx.Height;
        _srcX = regionPx.X - desc.DesktopCoordinates.Left;
        _srcY = regionPx.Y - desc.DesktopCoordinates.Top;

        var texDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_w,
            Height = (uint)_h,
            MipLevels = 1,
            ArraySize = 1,
            Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM — same layout as our RGB32 frames
            SampleCount = 1,
            SampleQuality = 0,
            Usage = 3,          // D3D11_USAGE_STAGING
            BindFlags = 0,
            CPUAccessFlags = 0x20000, // D3D11_CPU_ACCESS_READ
            MiscFlags = 0,
        };
        Marshal.ThrowExceptionForHR(_device.CreateTexture2D(ref texDesc, IntPtr.Zero, out _staging));

        Duplicate();
    }

    private void Duplicate()
    {
        Marshal.ThrowExceptionForHR(_output.DuplicateOutput(_device, out _dup));
    }

    /// <summary>
    /// Copies the newest desktop frame's region into <paramref name="bmp"/>.
    /// Returns false when nothing changed since the last call (bmp untouched).
    /// Throws when duplication is irrecoverably lost — caller falls back to GDI.
    /// </summary>
    public bool TryAccumulateInto(Bitmap bmp)
    {
        if (_dup is null) Duplicate();

        int hr = _dup!.AcquireNextFrame(8, out _, out var resource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            return false;
        if (hr == DXGI_ERROR_ACCESS_LOST)
        {
            // Mode change / fullscreen switch / lock screen: re-duplicate and
            // pick the frame up next tick.
            Release(_dup);
            _dup = null;
            Duplicate();
            return false;
        }
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var tex = (ID3D11Texture2D)resource;
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
            Release(resource);
            _dup.ReleaseFrame();
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
        Release(_dup);
        Release(_staging);
        Release(_output);
        Release(_ctx);
        Release(_device);
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            Marshal.ReleaseComObject(com);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out ID3D11Device device, out int featureLevel, out ID3D11DeviceContext context);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);
}

// ============================================================================
// Minimal D3D11/DXGI COM interop. Only the methods we call are declared with
// real signatures; `ReservedN` placeholders keep every other vtable slot in
// position (plain methods, one slot each — names must NOT start with
// "_VtblGap", which .NET parses as a multi-slot gap directive).
// ============================================================================

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DXGI_OUTPUT_DESC
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    public RECT DesktopCoordinates;
    public int AttachedToDesktop;
    public int Rotation;
    public IntPtr Monitor;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_FRAME_INFO
{
    public long LastPresentTime;
    public long LastMouseUpdateTime;
    public uint AccumulatedFrames;
    public int RectsCoalesced;
    public int ProtectedContentMaskedOut;
    public int PointerX, PointerY, PointerVisible;
    public uint TotalMetadataBufferSize;
    public uint PointerShapeBufferSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_TEXTURE2D_DESC
{
    public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality,
        Usage, BindFlags, CPUAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_BOX { public uint Left, Top, Front, Right, Bottom, Back; }

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_MAPPED_SUBRESOURCE
{
    public IntPtr pData;
    public uint RowPitch;
    public uint DepthPitch;
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("DB6F6DDB-AC77-4E88-8253-819DF9BBF140")]
internal interface ID3D11Device
{
    void Reserved3();  // CreateBuffer
    void Reserved4();  // CreateTexture1D
    [PreserveSig] int CreateTexture2D(ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out ID3D11Texture2D texture);
    void Reserved6();  // CreateTexture3D
    void Reserved7();  // CreateShaderResourceView
    void Reserved8();  // CreateUnorderedAccessView
    void Reserved9();  // CreateRenderTargetView
    void Reserved10(); // CreateDepthStencilView
    void Reserved11(); // CreateInputLayout
    void Reserved12(); // CreateVertexShader
    void Reserved13(); // CreateGeometryShader
    void Reserved14(); // CreateGeometryShaderWithStreamOutput
    void Reserved15(); // CreatePixelShader
    void Reserved16(); // CreateHullShader
    void Reserved17(); // CreateDomainShader
    void Reserved18(); // CreateComputeShader
    void Reserved19(); // CreateClassLinkage
    void Reserved20(); // CreateBlendState
    void Reserved21(); // CreateDepthStencilState
    void Reserved22(); // CreateRasterizerState
    void Reserved23(); // CreateSamplerState
    void Reserved24(); // CreateQuery
    void Reserved25(); // CreatePredicate
    void Reserved26(); // CreateCounter
    void Reserved27(); // CreateDeferredContext
    void Reserved28(); // OpenSharedResource
    void Reserved29(); // CheckFormatSupport
    void Reserved30(); // CheckMultisampleQualityLevels
    void Reserved31(); // CheckCounterInfo
    void Reserved32(); // CheckCounter
    void Reserved33(); // CheckFeatureSupport
    void Reserved34(); // GetPrivateData
    void Reserved35(); // SetPrivateData
    void Reserved36(); // SetPrivateDataInterface
    void Reserved37(); // GetFeatureLevel
    void Reserved38(); // GetCreationFlags
    void Reserved39(); // GetDeviceRemovedReason
    void Reserved40(); // GetImmediateContext
    void Reserved41(); // SetExceptionMode
    void Reserved42(); // GetExceptionMode
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C0BFA96C-E089-44FB-8EAF-26F8796190DA")]
internal interface ID3D11DeviceContext
{
    void Reserved3();  // GetDevice
    void Reserved4();  // GetPrivateData
    void Reserved5();  // SetPrivateData
    void Reserved6();  // SetPrivateDataInterface
    void Reserved7();  // VSSetConstantBuffers
    void Reserved8();  // PSSetShaderResources
    void Reserved9();  // PSSetShader
    void Reserved10(); // PSSetSamplers
    void Reserved11(); // VSSetShader
    void Reserved12(); // DrawIndexed
    void Reserved13(); // Draw
    [PreserveSig] int Map(ID3D11Texture2D resource, uint subresource, uint mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);
    [PreserveSig] void Unmap(ID3D11Texture2D resource, uint subresource);
    void Reserved16(); // PSSetConstantBuffers
    void Reserved17(); // IASetInputLayout
    void Reserved18(); // IASetVertexBuffers
    void Reserved19(); // IASetIndexBuffer
    void Reserved20(); // DrawIndexedInstanced
    void Reserved21(); // DrawInstanced
    void Reserved22(); // GSSetConstantBuffers
    void Reserved23(); // GSSetShader
    void Reserved24(); // IASetPrimitiveTopology
    void Reserved25(); // VSSetShaderResources
    void Reserved26(); // VSSetSamplers
    void Reserved27(); // Begin
    void Reserved28(); // End
    void Reserved29(); // GetData
    void Reserved30(); // SetPredication
    void Reserved31(); // GSSetShaderResources
    void Reserved32(); // GSSetSamplers
    void Reserved33(); // OMSetRenderTargets
    void Reserved34(); // OMSetRenderTargetsAndUnorderedAccessViews
    void Reserved35(); // OMSetBlendState
    void Reserved36(); // OMSetDepthStencilState
    void Reserved37(); // SOSetTargets
    void Reserved38(); // DrawAuto
    void Reserved39(); // DrawIndexedInstancedIndirect
    void Reserved40(); // DrawInstancedIndirect
    void Reserved41(); // Dispatch
    void Reserved42(); // DispatchIndirect
    void Reserved43(); // RSSetState
    void Reserved44(); // RSSetViewports
    void Reserved45(); // RSSetScissorRects
    [PreserveSig] void CopySubresourceRegion(ID3D11Texture2D dst, uint dstSubresource,
        uint dstX, uint dstY, uint dstZ, ID3D11Texture2D src, uint srcSubresource, ref D3D11_BOX box);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C")]
internal interface ID3D11Texture2D
{
    void Reserved3();  // GetDevice
    void Reserved4();  // GetPrivateData
    void Reserved5();  // SetPrivateData
    void Reserved6();  // SetPrivateDataInterface
    void Reserved7();  // GetType
    void Reserved8();  // SetEvictionPriority
    void Reserved9();  // GetEvictionPriority
    void Reserved10(); // GetDesc
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C")]
internal interface IDXGIDevice
{
    void Reserved3();  // SetPrivateData
    void Reserved4();  // SetPrivateDataInterface
    void Reserved5();  // GetPrivateData
    void Reserved6();  // GetParent
    [PreserveSig] int GetAdapter(out IDXGIAdapter adapter);
    void Reserved8();  // CreateSurface
    void Reserved9();  // QueryResourceResidency
    void Reserved10(); // SetGPUThreadPriority
    void Reserved11(); // GetGPUThreadPriority
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2411E7E1-12AC-4CCF-BD14-9798E8534DC0")]
internal interface IDXGIAdapter
{
    void Reserved3();  // SetPrivateData
    void Reserved4();  // SetPrivateDataInterface
    void Reserved5();  // GetPrivateData
    void Reserved6();  // GetParent
    [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    void Reserved8();  // GetDesc
    void Reserved9();  // CheckInterfaceSupport
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("AE02EEDB-C735-4690-8D52-5A8DC20213AA")]
internal interface IDXGIOutput
{
    void Reserved3();  // SetPrivateData
    void Reserved4();  // SetPrivateDataInterface
    void Reserved5();  // GetPrivateData
    void Reserved6();  // GetParent
    [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
    void Reserved8();  // GetDisplayModeList
    void Reserved9();  // FindClosestMatchingMode
    void Reserved10(); // WaitForVBlank
    void Reserved11(); // TakeOwnership
    void Reserved12(); // ReleaseOwnership
    void Reserved13(); // GetGammaControlCapabilities
    void Reserved14(); // SetGammaControl
    void Reserved15(); // GetGammaControl
    void Reserved16(); // SetDisplaySurface
    void Reserved17(); // GetDisplaySurfaceData
    void Reserved18(); // GetFrameStatistics
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00CDDEA8-939B-4B83-A340-A685226666CC")]
internal interface IDXGIOutput1
{
    void Reserved3();  // SetPrivateData
    void Reserved4();  // SetPrivateDataInterface
    void Reserved5();  // GetPrivateData
    void Reserved6();  // GetParent
    [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
    void Reserved8();  // GetDisplayModeList
    void Reserved9();  // FindClosestMatchingMode
    void Reserved10(); // WaitForVBlank
    void Reserved11(); // TakeOwnership
    void Reserved12(); // ReleaseOwnership
    void Reserved13(); // GetGammaControlCapabilities
    void Reserved14(); // SetGammaControl
    void Reserved15(); // GetGammaControl
    void Reserved16(); // SetDisplaySurface
    void Reserved17(); // GetDisplaySurfaceData
    void Reserved18(); // GetFrameStatistics
    void Reserved19(); // GetDisplayModeList1
    void Reserved20(); // FindClosestMatchingMode1
    void Reserved21(); // GetDisplaySurfaceData1
    [PreserveSig] int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("191CFAC3-A341-470D-B26E-A864F428319C")]
internal interface IDXGIOutputDuplication
{
    void Reserved3();  // SetPrivateData
    void Reserved4();  // SetPrivateDataInterface
    void Reserved5();  // GetPrivateData
    void Reserved6();  // GetParent
    void Reserved7();  // GetDesc
    [PreserveSig] int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo,
        [MarshalAs(UnmanagedType.IUnknown)] out object desktopResource);
    void Reserved9();  // GetFrameDirtyRects
    void Reserved10(); // GetFrameMoveRects
    void Reserved11(); // GetFramePointerShape
    void Reserved12(); // MapDesktopSurface
    void Reserved13(); // UnMapDesktopSurface
    [PreserveSig] int ReleaseFrame();
}
