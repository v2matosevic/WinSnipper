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

                // A seek lands on the preceding keyframe, so the first sample
                // back can be up to a whole GOP early — decode forward until we
                // reach the frame the strip actually wants, or the strip shows
                // the same picture several cells running.
                IMFSample? sample = null;
                bool eos = false;
                for (int guard = 0; guard < 120; guard++)
                {
                    Mf.Release(sample);
                    Mf.Check(reader.ReadSample(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                        out _, out uint flags, out _, out sample));
                    if ((flags & (Mf.MF_SOURCE_READERF_ENDOFSTREAM | Mf.MF_SOURCE_READERF_ERROR)) != 0)
                    {
                        eos = true;
                        break;
                    }
                    if (sample is null) continue;
                    if (sample.GetSampleTime(out long t) == 0 && t < target) continue;
                    break;
                }
                if (eos || sample is null)
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

    private static BitmapSource ToBitmap(IMFSample sample, int w, int h, int typeStride, int targetHeight)
    {
        var pixels = VideoFrames.ToPackedBytes(sample, w, h, typeStride);
        BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, w * 4);
        if (h > targetHeight)
        {
            double scale = targetHeight / (double)h;
            bmp = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
        }
        var frozen = BitmapFrame.Create(bmp);
        frozen.Freeze();
        return frozen;
    }
}
