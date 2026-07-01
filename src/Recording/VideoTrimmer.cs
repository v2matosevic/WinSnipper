using System.Runtime.InteropServices;

namespace WinSnipper.Recording;

/// <summary>
/// Cuts an MP4 down to [start, end] by decoding through a source reader and
/// re-encoding with a sink writer. Re-encoding (rather than copying compressed
/// samples) keeps cuts frame-accurate regardless of keyframe placement.
/// </summary>
public static class VideoTrimmer
{
    public static void Trim(string src, string dst, TimeSpan start, TimeSpan end, Action<double>? progress = null)
    {
        Mf.EnsureStartup();

        IMFAttributes? readerAttrs = null;
        IMFSourceReader? reader = null;
        IMFMediaType? rgb = null, decoded = null, native = null;
        IMFSinkWriter? writer = null;
        ICodecAPI? codec = null;

        try
        {
            // Reader that decodes the video stream to RGB32.
            Mf.Check(Mf.MFCreateAttributes(out readerAttrs, 1));
            var key = Mf.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
            Mf.Check(readerAttrs.SetUINT32(ref key, 1));
            Mf.Check(Mf.MFCreateSourceReaderFromURL(src, readerAttrs, out reader));

            Mf.Check(reader.SetStreamSelection(Mf.MF_SOURCE_READER_ALL_STREAMS, 0));
            Mf.Check(reader.SetStreamSelection(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 1));

            Mf.Check(Mf.MFCreateMediaType(out rgb));
            Mf.SetGuid(rgb, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            Mf.SetGuid(rgb, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_RGB32);
            Mf.Check(reader.SetCurrentMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, rgb));

            // The fully-resolved decoded type (size, stride, …) becomes the
            // writer's input type verbatim, so orientation always matches.
            Mf.Check(reader.GetCurrentMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out decoded));
            key = Mf.MF_MT_FRAME_SIZE;
            Mf.Check(decoded.GetUINT64(ref key, out ulong size));
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);

            // Frame rate comes from the source's native type (default 30 if absent).
            int fpsNum = 30, fpsDen = 1;
            if (reader.GetNativeMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out native) == 0)
            {
                key = Mf.MF_MT_FRAME_RATE;
                if (native.GetUINT64(ref key, out ulong rate) == 0 && (uint)rate != 0)
                {
                    fpsNum = (int)(rate >> 32);
                    fpsDen = (int)(rate & 0xFFFFFFFF);
                }
            }

            writer = ScreenRecorder.CreateWriter(dst, w, h, fpsNum, fpsDen, decoded, out int stream);
            codec = ScreenRecorder.GetCodecApi(writer, stream);
            int gop = Math.Max(1, fpsNum / Math.Max(1, fpsDen));
            int written = 0;

            // Seek near the start point; the reader resumes at the previous
            // keyframe, so early decoded frames are dropped below.
            var timeFormat = Guid.Empty;
            var pos = Mf.PropVariantI8.From(start.Ticks);
            Mf.Check(reader.SetCurrentPosition(ref timeFormat, ref pos));

            long startT = start.Ticks, endT = end.Ticks;
            double span = Math.Max(1, endT - startT);
            while (true)
            {
                Mf.Check(reader.ReadSample(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                    out _, out uint flags, out _, out IMFSample? sample));
                if ((flags & Mf.MF_SOURCE_READERF_ERROR) != 0)
                {
                    Mf.Release(sample);
                    throw new InvalidOperationException("Source reader reported a stream error while trimming.");
                }
                if ((flags & Mf.MF_SOURCE_READERF_ENDOFSTREAM) != 0) { Mf.Release(sample); break; }
                if (sample is null) continue;

                try
                {
                    Mf.Check(sample.GetSampleTime(out long t));
                    if (t < startT) continue;
                    if (t > endT) break;
                    if (codec is not null && written % gop == 0)
                        ScreenRecorder.ForceKeyFrame(codec);
                    Mf.Check(sample.SetSampleTime(t - startT));
                    Mf.Check(writer.WriteSample(stream, sample));
                    written++;
                    progress?.Invoke((t - startT) / span);
                }
                finally
                {
                    Mf.Release(sample);
                }
            }

            Mf.Check(writer.Finalize_());
            progress?.Invoke(1);
        }
        finally
        {
            Mf.Release(codec);
            Mf.Release(writer);
            Mf.Release(native);
            Mf.Release(decoded);
            Mf.Release(rgb);
            Mf.Release(reader);
            Mf.Release(readerAttrs);
        }
    }
}
