namespace WinSnipper.Recording;

/// <summary>
/// Cuts an MP4 down to [start, end] by decoding through a source reader and
/// re-encoding with a sink writer. Re-encoding (rather than copying compressed
/// samples) keeps cuts frame-accurate regardless of keyframe placement.
///
/// Decoded frames are re-packed into a tightly-strided top-down RGB32 buffer
/// before they reach the encoder. Handing the decoder's own buffer straight
/// through looks like it should work, but its row pitch is padded for
/// alignment while the encoder reads at the stride the media type declares —
/// which sheared every trimmed frame into diagonal smears.
/// </summary>
public static class VideoTrimmer
{
    /// <summary>
    /// Re-encoding at the source bitrate stacks a second generation of loss on
    /// top of the first. Spending 60% more bits keeps a trimmed clip visually
    /// indistinguishable from the take it came from.
    /// </summary>
    private const double ReencodeBitrateBoost = 1.6;

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

            Mf.Check(reader.GetCurrentMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out decoded));
            key = Mf.MF_MT_FRAME_SIZE;
            Mf.Check(decoded.GetUINT64(ref key, out ulong size));
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
            // H.264 needs even dimensions; the recorder already enforces this,
            // but an imported or older file might not.
            w &= ~1;
            h &= ~1;
            if (w < 2 || h < 2)
                throw new InvalidOperationException("Video has no usable frame size.");

            key = Mf.MF_MT_DEFAULT_STRIDE;
            int typeStride = decoded.GetUINT32(ref key, out uint strideRaw) == 0 ? unchecked((int)strideRaw) : w * 4;

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
            int fps = Math.Max(1, fpsNum / Math.Max(1, fpsDen));
            long frameDur = 10_000_000L * fpsDen / Math.Max(1, fpsNum);

            // inputType: null asks for the canonical packed top-down RGB32 input
            // that PackSample below produces.
            uint bitrate = (uint)Math.Min(int.MaxValue,
                (long)(ScreenRecorder.Bitrate(w, h, fps) * ReencodeBitrateBoost));
            writer = ScreenRecorder.CreateWriter(dst, w, h, fpsNum, fpsDen, null, out int stream, bitrate);
            codec = ScreenRecorder.GetCodecApi(writer, stream);
            int written = 0;

            // Seek near the start point; the reader resumes at the previous
            // keyframe, so early decoded frames are dropped below.
            var timeFormat = Guid.Empty;
            var pos = Mf.PropVariantI8.From(start.Ticks);
            Mf.Check(reader.SetCurrentPosition(ref timeFormat, ref pos));

            long startT = start.Ticks, endT = end.Ticks;
            double span = Math.Max(1, endT - startT);
            long firstKept = -1;

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

                // A mid-stream format change would invalidate w/h/stride and
                // silently corrupt everything after it. Re-read and bail out
                // rather than write garbage.
                if ((flags & Mf.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                {
                    Mf.Release(sample);
                    IMFMediaType? changed = null;
                    try
                    {
                        Mf.Check(reader.GetCurrentMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out changed));
                        key = Mf.MF_MT_FRAME_SIZE;
                        if (changed.GetUINT64(ref key, out ulong s2) == 0 &&
                            ((int)(s2 >> 32) & ~1) == w && ((int)(s2 & 0xFFFFFFFF) & ~1) == h)
                        {
                            key = Mf.MF_MT_DEFAULT_STRIDE;
                            if (changed.GetUINT32(ref key, out uint st2) == 0)
                                typeStride = unchecked((int)st2);
                            continue; // same geometry, just a new stride — keep going
                        }
                    }
                    finally
                    {
                        Mf.Release(changed);
                    }
                    break; // resolution changed mid-file: stop with what we have
                }

                if (sample is null) continue;

                try
                {
                    Mf.Check(sample.GetSampleTime(out long t));
                    if (t < startT) continue;
                    if (t > endT) break;
                    if (firstKept < 0) firstKept = t;

                    // Rebase on the first frame we actually keep, so the output
                    // always starts at zero even when the nearest decoded frame
                    // sits a few milliseconds past the in-point.
                    if (sample.GetSampleDuration(out long dur) != 0 || dur <= 0)
                        dur = frameDur;

                    if (codec is not null && written % fps == 0)
                        ScreenRecorder.ForceKeyFrame(codec);

                    var packed = VideoFrames.PackSample(sample, w, h, typeStride);
                    try
                    {
                        Mf.Check(packed.SetSampleTime(t - firstKept));
                        Mf.Check(packed.SetSampleDuration(dur));
                        Mf.Check(writer.WriteSample(stream, packed));
                    }
                    finally
                    {
                        Mf.Release(packed);
                    }
                    written++;
                    progress?.Invoke((t - startT) / span);
                }
                finally
                {
                    Mf.Release(sample);
                }
            }

            if (written == 0)
                throw new InvalidOperationException("No frames fell inside the selected range.");

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
