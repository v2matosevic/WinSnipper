using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinSnipper.Recording;

/// <summary>
/// Records a screen region to an H.264 MP4 using GDI capture + a Media
/// Foundation sink writer (hardware encoder when available). Runs its capture
/// loop on a dedicated background thread; frames carry wall-clock timestamps,
/// so an occasional slow frame just becomes a slightly longer one on playback.
/// </summary>
public sealed class ScreenRecorder
{
    private readonly string _path;
    private readonly Int32Rect _region;   // physical screen px, width/height already even

    /// <summary>The captured region in physical screen pixels.</summary>
    public Int32Rect Region => _region;
    private readonly int _fps;
    private readonly bool _cursor;

    private readonly Stopwatch _clock = new();
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _paused;
    private long _pausedTicks100;                 // total time spent paused, 100ns units
    private long _pauseStartedTicks100;
    private readonly TaskCompletionSource<BitmapSource?> _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _captureReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<bool> CaptureReady => _captureReady.Task;
    internal event Action<bool>? CaptureModeChanged;
    private volatile bool _excludesControlWindows;
    internal bool ExcludesControlWindows => _excludesControlWindows;

    public string FilePath => _path;
    public Exception? Error { get; private set; }
    internal Action? BeforeFinalize { get; set; }

    public ScreenRecorder(string path, Int32Rect regionPx, int fps, bool includeCursor)
    {
        _path = path;
        _region = new Int32Rect(regionPx.X, regionPx.Y, regionPx.Width & ~1, regionPx.Height & ~1);
        _fps = Math.Clamp(fps, 5, 60);
        _cursor = includeCursor;
    }

    /// <summary>Recorded time, excluding pauses.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            long t = _clock.Elapsed.Ticks - Interlocked.Read(ref _pausedTicks100);
            if (_paused) t -= _clock.Elapsed.Ticks - Interlocked.Read(ref _pauseStartedTicks100);
            return new TimeSpan(Math.Max(0, t));
        }
    }

    public bool IsPaused => _paused;

    public void Start()
    {
        // The clock starts at the first capture (inside the loop), not here —
        // encoder init takes a beat and the HUD timer must match the video.
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WinSnipper.Recorder" };
        _thread.Start();
    }

    public void SetPaused(bool paused)
    {
        if (paused == _paused) return;
        if (paused)
        {
            Interlocked.Exchange(ref _pauseStartedTicks100, _clock.Elapsed.Ticks);
            _paused = true;
        }
        else
        {
            Interlocked.Add(ref _pausedTicks100, _clock.Elapsed.Ticks - Interlocked.Read(ref _pauseStartedTicks100));
            _paused = false; // publish active state only after removing paused time
        }
    }

    /// <summary>
    /// Stops capturing and finalizes the MP4. Returns the last captured frame
    /// (for the floating thumbnail), or null if nothing was recorded.
    /// </summary>
    public Task<BitmapSource?> StopAsync()
    {
        _stop = true;
        return _finished.Task;
    }

    // ---------- capture thread ----------

    private void CaptureLoop()
    {
        IMFSinkWriter? writer = null;
        BitmapSource? lastFrame = null;
        int w = _region.Width, h = _region.Height;
        long frameDur = 10_000_000L / _fps;
        int frames = 0;

        // The H.264 encoder MFT re-stamps output at the declared frame rate
        // regardless of input timestamps, so the sample COUNT is the
        // timeline. We emit strictly constant-frame-rate video: one sample
        // per 1/fps slot, duplicating the latest capture whenever GDI
        // capture falls behind. Duplicates encode to near-zero bytes.
        long firstTs = -1;
        long slot = 0;

        IRegionCapture? cap = null;
        Bitmap? clean = null;
        ICodecAPI? codec = null;
        IntPtr mmcss = IntPtr.Zero;

        timeBeginPeriod(1); // default 15.6 ms sleep granularity would cap us near 20 fps
        try
        {
            Mf.EnsureStartup(); // native initialization belongs on the recording worker
            Diag($"start region={_region.X},{_region.Y} {w}x{h} fps={_fps}");
            var initClock = Stopwatch.StartNew();
            writer = CreateWriter(_path, w, h, _fps, 1, null, out int stream);
            // Belt and braces on top of the GOP request: explicitly mark one
            // frame per second as a keyframe (some hardware encoders accept
            // AVEncMPVGOPSize and then ignore it).
            codec = GetCodecApi(writer, stream);
            Diag($"writer ready in {initClock.ElapsedMilliseconds} ms");
            // The frame every sample is made from. A DIB with its own device
            // context: the cursor is drawn straight into it and the encoder
            // copy reads its memory, with no GDI+ round trip per frame.
            using var frame = new FrameSurface(w, h);

            // Scheduled like audio/video capture rather than like background
            // work, so a busy machine does not turn into dropped frames.
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            mmcss = AvSetMmThreadCharacteristicsW("Capture", out _);

            cap = CreateBestCapture(_region);
            if (cap is not null)
            {
                // Seed with one GDI grab so ticks before the first real frame
                // (and fully static screens) still show the desktop.
                clean = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                using var cg = Graphics.FromImage(clean);
                cg.CopyFromScreen(_region.X, _region.Y, 0, 0,
                    new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
            }

#if WINRT
            _excludesControlWindows = cap is WgcCapture;
#else
            _excludesControlWindows = false;
#endif
            _captureReady.TrySetResult(_excludesControlWindows);

            // What the encoder was last given. An unchanged screen, and every
            // slot a late frame has to fill, reuse it: no copy, no allocation.
            IMFMediaBuffer? pixels = null;
            CursorState lastCursor = default;
            int unique = 0, lateSlots = 0;
            long workTicks = 0, maxWorkTicks = 0;
            string backend = cap?.GetType().Name ?? "Gdi";

            _clock.Start();
            try
            {
                while (!_stop)
                {
                    if (_paused)
                    {
                        Thread.Sleep(40);
                        continue;
                    }

                    long ts = ActiveTicks100();
                    if (firstTs < 0) firstTs = ts;
                    ts -= firstTs;
                    long workStart = Stopwatch.GetTimestamp();

                    bool changed;
                    if (cap is not null)
                    {
                        try
                        {
                            changed = cap.TryAccumulateInto(clean!); // false = unchanged, clean already holds the frame
                        }
                        catch (Exception ex)
                        {
                            Diag($"capture backend lost, switching to GDI: {ex.Message}");
                            // The HUD must clear capture affinity BEFORE the first
                            // compatibility frame, otherwise it can become a black box.
                            _excludesControlWindows = false;
                            CaptureModeChanged?.Invoke(false);
                            cap.Dispose();
                            cap = null;
                            backend += "+Gdi";
                            changed = true;
                        }
                    }
                    else changed = true; // GDI has no change signal: copy the screen every frame

                    var cursor = _cursor ? CursorState.Read() : default;
                    if (pixels is null || changed || !cursor.Equals(lastCursor))
                    {
                        if (cap is not null) frame.CopyFrom(clean!);
                        else frame.CopyScreen(_region.X, _region.Y);
                        if (_cursor) frame.DrawCursor(cursor, _region.X, _region.Y);
                        lastCursor = cursor;
                        Mf.Release(pixels);
                        pixels = CreateBuffer(frame);
                        unique++;
                    }

                    // Fill every slot that has come due, at least one.
                    long due = Math.Max(slot, ts / frameDur);
                    if (due > slot) lateSlots += (int)(due - slot);
                    for (; slot <= due; slot++)
                    {
                        if (codec is not null && slot % _fps == 0)
                            ForceKeyFrame(codec);
                        Mf.Check(Mf.MFCreateSample(out var sample));
                        try
                        {
                            Mf.Check(sample.AddBuffer(pixels));
                            Mf.Check(sample.SetSampleTime(slot * frameDur));
                            Mf.Check(sample.SetSampleDuration(frameDur));
                            Mf.Check(writer.WriteSample(stream, sample));
                        }
                        finally { Mf.Release(sample); }
                        frames++;
                    }

                    long work = Stopwatch.GetTimestamp() - workStart;
                    workTicks += work;
                    if (work > maxWorkTicks) maxWorkTicks = work;

                    // Sleep until the next slot opens (measured in unpaused time).
                    long sleepMs = (firstTs + slot * frameDur - ActiveTicks100()) / 10_000;
                    if (sleepMs > 0) Thread.Sleep((int)sleepMs);
                }
            }
            finally { Mf.Release(pixels); }

            if (frames > 0)
            {
                lastFrame = frame.ToBitmapSource();
                BeforeFinalize?.Invoke();
                Mf.Check(writer.Finalize_());
            }
            Diag($"loop exit frames={frames} elapsed={Elapsed.TotalSeconds:0.00}s");
            // unique = frames with new content; late-slots = frames the loop was
            // too slow to capture and filled with a repeat (visible stutter).
            int loops = Math.Max(1, frames - lateSlots);
            double toMs = 1000.0 / Stopwatch.Frequency;
            Diag($"stats backend={backend} unique={unique} late-slots={lateSlots} avg-work={workTicks * toMs / loops:0.0}ms max-work={maxWorkTicks * toMs:0.0}ms budget={1000.0 / _fps:0.0}ms");
        }
        catch (Exception ex)
        {
            Error = ex;
            Util.LogCrash("Recorder", ex);
        }
        finally
        {
            _captureReady.TrySetResult(false); // startup failures must not strand the UI awaiting readiness
            timeEndPeriod(1);
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            cap?.Dispose();
            clean?.Dispose();
            Mf.Release(codec);
            Mf.Release(writer);
            if (frames == 0 || Error is not null)
            {
                try { if (System.IO.File.Exists(_path) && frames == 0) System.IO.File.Delete(_path); } catch { }
                lastFrame = null;
            }
            _finished.TrySetResult(lastFrame);
        }
    }

    private long ActiveTicks100() => _clock.Elapsed.Ticks - Interlocked.Read(ref _pausedTicks100);

    /// <summary>Appends to %APPDATA%\WinSnipper\recorder.log (trimmed at ~256 KB).</summary>
    internal static void Diag(string message)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSnipper");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "recorder.log");
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 256_000)
                System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Capture ladder: Windows.Graphics.Capture (sees MPO overlay video; WinRT
    /// flavor only) → DXGI Desktop Duplication → GDI BitBlt (null).
    /// </summary>
    private static IRegionCapture? CreateBestCapture(Int32Rect region)
    {
#if WINRT
        try
        {
            var wgc = new WgcCapture(region);
            Diag("capture: windows.graphics.capture");
            return wgc;
        }
        catch (Exception ex)
        {
            Diag($"wgc unavailable: {ex.Message}");
        }
#endif
        try
        {
            var dd = new DesktopDuplicator(region);
            Diag("capture: desktop duplication");
            return dd;
        }
        catch (Exception ex)
        {
            Diag($"dd unavailable: {ex.Message}");
        }
        Diag("capture: GDI");
        return null;
    }

    /// <summary>Copies the frame into a new media buffer the encoder can keep reading.</summary>
    private static IMFMediaBuffer CreateBuffer(FrameSurface frame)
    {
        int cb = frame.Stride * frame.Height;
        Mf.Check(Mf.MFCreateMemoryBuffer(cb, out var buffer));
        try
        {
            Mf.Check(buffer.Lock(out IntPtr dst, IntPtr.Zero, IntPtr.Zero));
            CopyMemory(dst, frame.Bits, (UIntPtr)cb);
            Mf.Check(buffer.Unlock());
            Mf.Check(buffer.SetCurrentLength(cb));
            return buffer;
        }
        catch
        {
            Mf.Release(buffer);
            throw;
        }
    }
    /// <summary>
    /// H.264/MP4 sink writer (RGB32 top-down input unless <paramref name="inputType"/>
    /// is given), with BeginWriting already called. Prefers the hardware
    /// encoder, but falls back to the software one when the hardware MFT
    /// refuses a short GOP — one keyframe per second is what keeps seeking
    /// and trim-scrubbing responsive, and some hardware ICodecAPI
    /// implementations are stubs.
    /// </summary>
    internal static IMFSinkWriter CreateWriter(string path, int w, int h, int fpsNum, int fpsDen,
        IMFMediaType? inputType, out int streamIndex, uint bitrate = 0)
    {
        int gop = Math.Max(1, fpsNum / Math.Max(1, fpsDen));

        var writer = CreateWriterCore(path, w, h, fpsNum, fpsDen, inputType, hardware: true, out streamIndex, bitrate);
        if (TrySetGopSize(writer, streamIndex, gop))
        {
            Mf.Check(writer.BeginWriting());
            return writer;
        }
        Mf.Check(writer.BeginWriting());
        if (TrySetGopSize(writer, streamIndex, gop)) // some encoders only accept it while streaming
            return writer;

        Diag("gop: hardware encoder refused, using software encoder");
        Mf.Release(writer); // recreating from the URL truncates the file
        writer = CreateWriterCore(path, w, h, fpsNum, fpsDen, inputType, hardware: false, out streamIndex, bitrate);
        TrySetGopSize(writer, streamIndex, gop);
        Mf.Check(writer.BeginWriting());
        return writer;
    }

    private static IMFSinkWriter CreateWriterCore(string path, int w, int h, int fpsNum, int fpsDen,
        IMFMediaType? inputType, bool hardware, out int streamIndex, uint bitrate)
    {
        Mf.Check(Mf.MFCreateAttributes(out var attrs, 2));
        var key = Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
        Mf.Check(attrs.SetUINT32(ref key, hardware ? 1u : 0u));
        key = Mf.MF_SINK_WRITER_DISABLE_THROTTLING;
        Mf.Check(attrs.SetUINT32(ref key, 1));

        int fps = Math.Max(1, fpsNum / Math.Max(1, fpsDen));
        IMFMediaType? outType = null, inType = null;
        try
        {
            Mf.Check(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out var writer));

            Mf.Check(Mf.MFCreateMediaType(out outType));
            Mf.SetGuid(outType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            Mf.SetGuid(outType, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_H264);
            key = Mf.MF_MT_AVG_BITRATE;
            Mf.Check(outType.SetUINT32(ref key, bitrate != 0 ? bitrate : Bitrate(w, h, fps)));
            key = Mf.MF_MT_INTERLACE_MODE;
            Mf.Check(outType.SetUINT32(ref key, Mf.MFVideoInterlace_Progressive));
            key = Mf.MF_MT_MAX_KEYFRAME_SPACING;
            Mf.Check(outType.SetUINT32(ref key, (uint)fps));
            Mf.SetSize(outType, Mf.MF_MT_FRAME_SIZE, w, h);
            Mf.SetRatio(outType, Mf.MF_MT_FRAME_RATE, fpsNum, fpsDen);
            Mf.SetRatio(outType, Mf.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            Mf.Check(writer.AddStream(outType, out streamIndex));

            if (inputType is not null)
            {
                Mf.Check(writer.SetInputMediaType(streamIndex, inputType, null));
                return writer;
            }

            Mf.Check(Mf.MFCreateMediaType(out inType));
            Mf.SetGuid(inType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            Mf.SetGuid(inType, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_RGB32);
            key = Mf.MF_MT_INTERLACE_MODE;
            Mf.Check(inType.SetUINT32(ref key, Mf.MFVideoInterlace_Progressive));
            key = Mf.MF_MT_ALL_SAMPLES_INDEPENDENT;
            Mf.Check(inType.SetUINT32(ref key, 1));
            key = Mf.MF_MT_DEFAULT_STRIDE;
            Mf.Check(inType.SetUINT32(ref key, (uint)(w * 4))); // positive stride = top-down rows
            Mf.SetSize(inType, Mf.MF_MT_FRAME_SIZE, w, h);
            Mf.SetRatio(inType, Mf.MF_MT_FRAME_RATE, fpsNum, fpsDen);
            Mf.SetRatio(inType, Mf.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            Mf.Check(writer.SetInputMediaType(streamIndex, inType, null));
            return writer;
        }
        finally
        {
            Mf.Release(attrs);
            Mf.Release(outType);
            Mf.Release(inType);
        }
    }

    /// <summary>~0.1 bits per pixel per frame — crisp for screen content, small files.</summary>
    internal static uint Bitrate(int w, int h, int fps) =>
        (uint)Math.Clamp((long)(w * (double)h * fps * 0.10), 1_000_000, 16_000_000);

    /// <summary>The encoder MFT's codec configuration interface, or null.</summary>
    internal static ICodecAPI? GetCodecApi(IMFSinkWriter writer, int streamIndex)
    {
        try
        {
            var service = Guid.Empty;
            var iid = typeof(ICodecAPI).GUID;
            if (writer.GetServiceForStream(streamIndex, ref service, ref iid, out IntPtr p) != 0)
                return null;
            var codec = (ICodecAPI)Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);
            return codec;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Marks the next encoded frame as an IDR keyframe (best effort).</summary>
    internal static void ForceKeyFrame(ICodecAPI codec)
    {
        try
        {
            var key = Mf.CODECAPI_AVEncVideoForceKeyFrame;
            var value = Mf.Variant.FromUInt32(1);
            codec.SetValue(ref key, ref value);
        }
        catch { }
    }

    /// <summary>
    /// Ask the encoder for one keyframe per second via ICodecAPI (the media
    /// type's MF_MT_MAX_KEYFRAME_SPACING is ignored by the H.264 MFT).
    /// Returns true when the encoder accepted it.
    /// </summary>
    private static bool TrySetGopSize(IMFSinkWriter writer, int streamIndex, int gopFrames)
    {
        try
        {
            var codec = GetCodecApi(writer, streamIndex);
            if (codec is null)
            {
                Diag("gop: encoder exposes no ICodecAPI");
                return false;
            }
            var key = Mf.CODECAPI_AVEncMPVGOPSize;
            var value = Mf.Variant.FromUInt32((uint)gopFrames);
            int hr = codec.SetValue(ref key, ref value);
            Diag(hr == 0 ? $"gop: {gopFrames} frames" : $"gop: SetValue failed 0x{hr:X8}");
            Mf.Release(codec);
            return hr == 0;
        }
        catch (Exception ex)
        {
            Diag($"gop: {ex.Message}");
            return false;
        }
    }

    // ---------- P/Invoke ----------

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, out uint index);

    [DllImport("avrt.dll")]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint ms);
}
