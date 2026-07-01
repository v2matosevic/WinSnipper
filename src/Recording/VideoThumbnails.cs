using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSnipper.Recording;

/// <summary>
/// Extracts evenly spaced preview frames from a video for the trim window's
/// filmstrip, via a Media Foundation source reader.
/// </summary>
public static class VideoThumbnails
{
    public static (List<BitmapSource> frames, TimeSpan duration) Extract(string path, int count, int targetHeight)
    {
        Mf.EnsureStartup();

        IMFAttributes? attrs = null;
        IMFSourceReader? reader = null;
        IMFMediaType? rgb = null, current = null;
        var frames = new List<BitmapSource>(count);

        try
        {
            Mf.Check(Mf.MFCreateAttributes(out attrs, 1));
            var key = Mf.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
            Mf.Check(attrs.SetUINT32(ref key, 1));
            Mf.Check(Mf.MFCreateSourceReaderFromURL(path, attrs, out reader));

            Mf.Check(reader.SetStreamSelection(Mf.MF_SOURCE_READER_ALL_STREAMS, 0));
            Mf.Check(reader.SetStreamSelection(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 1));

            Mf.Check(Mf.MFCreateMediaType(out rgb));
            Mf.SetGuid(rgb, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            Mf.SetGuid(rgb, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_RGB32);
            Mf.Check(reader.SetCurrentMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, rgb));

            Mf.Check(reader.GetCurrentMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out current));
            key = Mf.MF_MT_FRAME_SIZE;
            Mf.Check(current.GetUINT64(ref key, out ulong size));
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
            key = Mf.MF_MT_DEFAULT_STRIDE;
            int stride = current.GetUINT32(ref key, out uint strideRaw) == 0 ? unchecked((int)strideRaw) : w * 4;

            var duration = ReadDuration(reader);
            if (duration <= TimeSpan.Zero)
                return (frames, TimeSpan.Zero);

            var timeFormat = Guid.Empty;
            for (int i = 0; i < count; i++)
            {
                long target = (long)(duration.Ticks * (i + 0.5) / count);
                var pos = Mf.PropVariantI8.From(target);
                Mf.Check(reader.SetCurrentPosition(ref timeFormat, ref pos));

                Mf.Check(reader.ReadSample(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                    out _, out uint flags, out _, out IMFSample? sample));
                if ((flags & Mf.MF_SOURCE_READERF_ENDOFSTREAM) != 0 || sample is null)
                {
                    Mf.Release(sample);
                    break;
                }
                try
                {
                    frames.Add(ToBitmap(sample, w, h, stride, targetHeight));
                }
                finally
                {
                    Mf.Release(sample);
                }
            }
            return (frames, duration);
        }
        finally
        {
            Mf.Release(current);
            Mf.Release(rgb);
            Mf.Release(reader);
            Mf.Release(attrs);
        }
    }

    private static TimeSpan ReadDuration(IMFSourceReader reader)
    {
        var key = new Guid("6C990D33-BB8E-477A-8598-0D5D96FCD88A"); // MF_PD_DURATION (VT_UI8, 100ns)
        IntPtr pv = Marshal.AllocHGlobal(24); // PROPVARIANT
        try
        {
            if (reader.GetPresentationAttribute(0xFFFFFFFF /* MF_SOURCE_READER_MEDIASOURCE */, ref key, pv) != 0)
                return TimeSpan.Zero;
            long ticks = Marshal.ReadInt64(pv, 8);
            return new TimeSpan(ticks);
        }
        finally
        {
            Marshal.FreeHGlobal(pv);
        }
    }

    private static BitmapSource ToBitmap(IMFSample sample, int w, int h, int stride, int targetHeight)
    {
        Mf.Check(sample.ConvertToContiguousBuffer(out var buffer));
        try
        {
            Mf.Check(buffer.Lock(out IntPtr data, IntPtr.Zero, IntPtr.Zero));
            try
            {
                BitmapSource bmp;
                if (stride >= 0)
                {
                    bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, data, stride * h, stride);
                }
                else
                {
                    // Negative stride = bottom-up: flip rows into a managed buffer.
                    int rowBytes = w * 4;
                    var pixels = new byte[rowBytes * h];
                    for (int row = 0; row < h; row++)
                        Marshal.Copy(data + (h - 1 - row) * rowBytes, pixels, row * rowBytes, rowBytes);
                    bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, rowBytes);
                }
                if (h > targetHeight)
                {
                    double scale = targetHeight / (double)h;
                    bmp = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                }
                var frozen = BitmapFrame.Create(bmp);
                frozen.Freeze();
                return frozen;
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Mf.Release(buffer);
        }
    }
}
