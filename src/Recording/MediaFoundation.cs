using System.Runtime.InteropServices;

namespace WinSnipper.Recording;

/// <summary>
/// Minimal Media Foundation interop — just enough to drive an H.264 MP4
/// SinkWriter (recording) and a SourceReader (trimming). Hand-rolled so the
/// lite build stays dependency-free; vtable order of the COM interfaces is
/// load-bearing, do not reorder methods.
/// </summary>
internal static class Mf
{
    private const int MF_VERSION = 0x0002_0070;
    private static bool _started;

    /// <summary>Idempotent MFStartup; lives for the process lifetime.</summary>
    public static void EnsureStartup()
    {
        if (_started) return;
        int hr = MFStartup(MF_VERSION, 0);
        Marshal.ThrowExceptionForHR(hr);
        _started = true;
    }

    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            Marshal.ReleaseComObject(com);
    }

    // ---------- attribute / format GUIDs ----------

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    public static readonly Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("C9173739-5E56-461C-B713-46FB995CB95F");
    public static readonly Guid MF_MT_MAX_KEYFRAME_SPACING = new("C16EB52B-73A1-476F-8D62-839D6A020652");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");

    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("A634A91C-822B-41B9-A494-4DE4643612B0");
    public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("FB394F3D-CCF1-42EE-BBB3-F9B845D5681D");
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08B845D8-2B74-4AFE-9D53-BE16D2D5AE4F");

    public const int MFVideoInterlace_Progressive = 2;

    // Source reader pseudo-stream indexes / flags.
    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    public const uint MF_SOURCE_READERF_ERROR = 0x1;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;

    // ---------- exports ----------

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType type);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    public static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes? attributes,
        out IMFSinkWriter writer);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    public static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes? attributes,
        out IMFSourceReader reader);

    // ---------- helpers ----------

    public static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    public static void SetSize(IMFMediaType type, Guid key, int width, int height) =>
        Check(type.SetUINT64(ref key, ((ulong)(uint)width << 32) | (uint)height));

    public static void SetRatio(IMFMediaType type, Guid key, int numerator, int denominator) =>
        Check(type.SetUINT64(ref key, ((ulong)(uint)numerator << 32) | (uint)denominator));

    public static void SetGuid(IMFAttributes attrs, Guid key, Guid value) =>
        Check(attrs.SetGUID(ref key, ref value));

    public static void SetGuid(IMFMediaType type, Guid key, Guid value) =>
        Check(type.SetGUID(ref key, ref value));

    /// <summary>PROPVARIANT holding a VT_I8, for IMFSourceReader::SetCurrentPosition.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariantI8
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public long value;

        public static PropVariantI8 From(long v) => new() { vt = 20 /* VT_I8 */, value = v };
    }

    /// <summary>Full 24-byte VARIANT, for ICodecAPI::SetValue.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Variant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public long data1;
        public long data2;

        public static Variant FromUInt32(uint v) => new() { vt = 19 /* VT_UI4 */, data1 = v };
    }
}

// ============================================================================
// COM interfaces. IMFMediaType and IMFSample derive from IMFAttributes, so the
// full IMFAttributes vtable is repeated inline (COM interop requires it).
// All methods use [PreserveSig] so failed HRESULTs don't throw where we want
// to handle or ignore them.
// ============================================================================

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length);
    [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, int size, IntPtr blobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, byte[] buf, int size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes dest);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
internal interface IMFMediaType
{
    // IMFAttributes
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length);
    [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, int size, IntPtr blobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, byte[] buf, int size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes dest);
    // IMFMediaType
    [PreserveSig] int GetMajorType(out Guid majorType);
    [PreserveSig] int IsCompressedFormat(out int compressed);
    [PreserveSig] int IsEqual(IMFMediaType other, out int flags);
    [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr representation);
    [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr representation);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
internal interface IMFSample
{
    // IMFAttributes
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length);
    [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, int size, IntPtr blobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, byte[] buf, int size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes dest);
    // IMFSample
    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out int count);
    [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(int index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, IntPtr maxLength, IntPtr currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int length);
    [PreserveSig] int SetCurrentLength(int length);
    [PreserveSig] int GetMaxLength(out int length);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D")]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType targetType, out int streamIndex);
    [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType inputType, IMFAttributes? parameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
    [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
    [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
    [PreserveSig] int NotifyEndOfSegment(int streamIndex);
    [PreserveSig] int Flush(int streamIndex);
    [PreserveSig] int Finalize_(); // trailing underscore avoids Object.Finalize clash; vtable slot is what matters
    [PreserveSig] int GetServiceForStream(int streamIndex, ref Guid service, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetStatistics(int streamIndex, IntPtr stats);
}

/// <summary>Codec configuration on the encoder MFT (only SetValue is used).</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA")]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    void Reserved4(); // IsModifiable
    void Reserved5(); // GetParameterRange
    void Reserved6(); // GetParameterValues
    void Reserved7(); // GetDefaultValue
    void Reserved8(); // GetValue
    [PreserveSig] int SetValue(ref Guid api, ref Mf.Variant value);
    void Reserved10(); // RegisterForEvent
    void Reserved11(); // UnregisterForEvent
    void Reserved12(); // SetAllDefaults
    void Reserved13(); // SetValueWithNotify
    void Reserved14(); // SetAllDefaultsWithNotify
    void Reserved15(); // GetAllSettings
    void Reserved16(); // SetAllSettings
    void Reserved17(); // SetAllSettingsWithNotify
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993")]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint streamIndex, out int selected);
    [PreserveSig] int SetStreamSelection(uint streamIndex, int selected);
    [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType type);
    [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType type);
    [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType type);
    [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, ref Mf.PropVariantI8 position);
    [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex,
        out uint streamFlags, out long timestamp, out IMFSample? sample);
    [PreserveSig] int Flush(uint streamIndex);
    [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid attribute, IntPtr value);
}
