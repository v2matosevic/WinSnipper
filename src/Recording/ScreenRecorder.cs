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

    public string FilePath => _path;
    public Exception? Error { get; private set; }

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
        Mf.EnsureStartup();
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
            _paused = false;
            Interlocked.Add(ref _pausedTicks100, _clock.Elapsed.Ticks - Interlocked.Read(ref _pauseStartedTicks100));
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

        timeBeginPeriod(1); // default 15.6 ms sleep granularity would cap us near 20 fps
        try
        {
            Diag($"start region={_region.X},{_region.Y} {w}x{h} fps={_fps}");
            var initClock = Stopwatch.StartNew();
            writer = CreateWriter(_path, w, h, _fps, out int stream);
            Diag($"writer ready in {initClock.ElapsedMilliseconds} ms");
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(bmp);

            _clock.Start();
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

                CaptureFrame(g, bmp);

                // Fill every slot that has come due, at least one.
                long due = Math.Max(slot, ts / frameDur);
                for (; slot <= due; slot++)
                {
                    var sample = CreateSample(bmp, w, h);
                    Mf.Check(sample.SetSampleTime(slot * frameDur));
                    Mf.Check(sample.SetSampleDuration(frameDur));
                    Mf.Check(writer.WriteSample(stream, sample));
                    Mf.Release(sample);
                    frames++;
                }

                // Sleep until the next slot opens (measured in unpaused time).
                long sleepMs = (firstTs + slot * frameDur - ActiveTicks100()) / 10_000;
                if (sleepMs > 0) Thread.Sleep((int)sleepMs);
            }

            if (frames > 0)
            {
                lastFrame = ToBitmapSource(bmp);
                Mf.Check(writer.Finalize_());
            }
            Diag($"loop exit frames={frames} elapsed={Elapsed.TotalSeconds:0.00}s");
        }
        catch (Exception ex)
        {
            Error = ex;
            Util.LogCrash("Recorder", ex);
        }
        finally
        {
            timeEndPeriod(1);
            Mf.Release(writer);
            if (frames == 0 || Error is not null)
            {
                try { if (System.IO.File.Exists(_path) && frames == 0) System.IO.File.Delete(_path); } catch { }
                if (frames == 0) lastFrame = null;
            }
            _finished.TrySetResult(lastFrame);
        }
    }

    private long ActiveTicks100() => _clock.Elapsed.Ticks - Interlocked.Read(ref _pausedTicks100);

    /// <summary>Appends to %APPDATA%\WinSnipper\recorder.log (trimmed at ~256 KB).</summary>
    private static void Diag(string message)
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

    private void CaptureFrame(Graphics g, Bitmap bmp)
    {
        g.CopyFromScreen(_region.X, _region.Y, 0, 0, new System.Drawing.Size(bmp.Width, bmp.Height), CopyPixelOperation.SourceCopy);
        if (_cursor) DrawCursor(g);
    }

    private void DrawCursor(Graphics g)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != 1 /* CURSOR_SHOWING */) return;

        int x = ci.ptScreenPos.X - _region.X;
        int y = ci.ptScreenPos.Y - _region.Y;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            x -= ii.xHotspot;
            y -= ii.yHotspot;
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }

        IntPtr hdc = g.GetHdc();
        try { DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3 /* DI_NORMAL */); }
        finally { g.ReleaseHdc(hdc); }
    }

    /// <summary>Copies the bitmap into a fresh MF sample (time/duration set by the caller).</summary>
    private static IMFSample CreateSample(Bitmap bmp, int w, int h)
    {
        int cb = w * h * 4;
        Mf.Check(Mf.MFCreateMemoryBuffer(cb, out var buffer));
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            Mf.Check(buffer.Lock(out IntPtr dst, IntPtr.Zero, IntPtr.Zero));
            if (data.Stride == w * 4)
            {
                CopyMemory(dst, data.Scan0, (UIntPtr)cb);
            }
            else
            {
                for (int row = 0; row < h; row++)
                    CopyMemory(dst + row * w * 4, data.Scan0 + row * data.Stride, (UIntPtr)(w * 4));
            }
            Mf.Check(buffer.Unlock());
            Mf.Check(buffer.SetCurrentLength(cb));

            Mf.Check(Mf.MFCreateSample(out var sample));
            Mf.Check(sample.AddBuffer(buffer));
            return sample;
        }
        finally
        {
            bmp.UnlockBits(data);
            Mf.Release(buffer);
        }
    }

    /// <summary>H.264/MP4 sink writer with an RGB32 top-down input stream.</summary>
    internal static IMFSinkWriter CreateWriter(string path, int w, int h, int fps, out int streamIndex)
    {
        Mf.Check(Mf.MFCreateAttributes(out var attrs, 2));
        var key = Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
        Mf.Check(attrs.SetUINT32(ref key, 1));
        key = Mf.MF_SINK_WRITER_DISABLE_THROTTLING;
        Mf.Check(attrs.SetUINT32(ref key, 1));

        IMFMediaType? outType = null, inType = null;
        try
        {
            Mf.Check(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out var writer));

            Mf.Check(Mf.MFCreateMediaType(out outType));
            Mf.SetGuid(outType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            Mf.SetGuid(outType, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_H264);
            key = Mf.MF_MT_AVG_BITRATE;
            Mf.Check(outType.SetUINT32(ref key, Bitrate(w, h, fps)));
            key = Mf.MF_MT_INTERLACE_MODE;
            Mf.Check(outType.SetUINT32(ref key, Mf.MFVideoInterlace_Progressive));
            Mf.SetSize(outType, Mf.MF_MT_FRAME_SIZE, w, h);
            Mf.SetRatio(outType, Mf.MF_MT_FRAME_RATE, fps, 1);
            Mf.SetRatio(outType, Mf.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            Mf.Check(writer.AddStream(outType, out streamIndex));

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
            Mf.SetRatio(inType, Mf.MF_MT_FRAME_RATE, fps, 1);
            Mf.SetRatio(inType, Mf.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            Mf.Check(writer.SetInputMediaType(streamIndex, inType, null));

            Mf.Check(writer.BeginWriting());
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

    private static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon,
        int cx, int cy, int istep, IntPtr flicker, int flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint ms);
}
