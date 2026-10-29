using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinSnipper;

/// <summary>Orchestrates one snip: capture → overlay selection → save + clipboard → floating thumbnail.</summary>
public sealed class SnipManager
{
    private bool _active;
    private SnipOverlay? _prepared;
    private readonly HashSet<string> _usedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<PerformanceTrace, Task<(BitmapSource, Int32Rect)>> _capture;

    public SnipManager() : this(trace => CaptureWorker.CaptureAsync(trace)) { }
    internal SnipManager(Func<PerformanceTrace, Task<(BitmapSource, Int32Rect)>> capture) => _capture = capture;

    public void PrepareOverlay()
    {
        if (_active || SnipOverlay.IsOpen || _prepared is { IsClosed: false }) return;
        try { _prepared = SnipOverlay.PrepareScreenshot(); }
        catch (Exception ex) { Util.LogCrash("Prepare overlay", ex); }
    }

    public async void StartSnip() => await StartSnipAsync(new PerformanceTrace("snip-menu"));

    public async Task StartSnipAsync(PerformanceTrace trace)
    {
        if (_active || SnipOverlay.IsOpen) { trace.Finish("already-open"); return; }
        using var reservation = SnipOverlay.TryReserveCapture();
        if (reservation is null) { trace.Finish("already-open"); return; }
        using var boost = Responsiveness.Interactive();
        _active = true;
        SnipOverlay? overlay = null;
        try
        {
            // Hide existing thumbnails so they are not baked into the new screenshot.
            FloatingThumb.SetAllVisible(false);
            BitmapSource shot;
            Int32Rect bounds;
            try
            {
                trace.Mark("capture-queued");
                (shot, bounds) = await _capture(trace);
                trace.Mark("captured");
            }
            catch (Exception ex)
            {
                trace.Finish("capture-failed");
                Util.LogCrash("Screenshot capture", ex);
                throw;
            }

            if (Application.Current.Dispatcher.HasShutdownStarted) return;
            overlay = _prepared is { IsClosed: false } ? _prepared : SnipOverlay.PrepareScreenshot();
            _prepared = overlay;
            overlay.SetScreenshot(shot, bounds);
            trace.TrackWindow(overlay);
            bool ok = overlay.ShowScreenshotSelection();
            FloatingThumb.SetAllVisible(true);

            if (ok && overlay.SelectionPx is { Width: > 0, Height: > 0 } sel)
            {
                // The selector shows the reusable capture surface, which the
                // next capture overwrites; everything after this keeps a copy.
                var cropped = ScreenCapture.CopyRegion(shot, sel);

                // Clipboard first, and no disk access on this thread: the save
                // folder can be a hard drive that has spun down, and a paste
                // right after the selection must not wait for it.
                var copying = Settings.Current.CopyToClipboard ? Util.SetClipboardAsync(cropped) : null;

                string path = NextSnipPath();
                // Encode and write on a worker. PNG compression of a
                // full-screen selection is long enough to be felt, and nothing
                // about it needs to happen before the thumbnail appears —
                // anything that does need the file waits on this task first.
                // The worker also settles name clashes with files on disk.
                var saving = Task.Run(() => Util.SaveNewPng(cropped, path));

                // Selection is relative to the captured bitmap; the thumbnail
                // docks to whichever monitor the selection's centre falls on.
                var anchor = new System.Drawing.Point(
                    bounds.X + sel.X + sel.Width / 2,
                    bounds.Y + sel.Y + sel.Height / 2);
                new FloatingThumb(path, cropped, anchor: anchor, saving: saving, copying: copying).ShowStacked();
            }
        }
        finally
        {
            try { overlay?.ClearScreenshot(); }
            finally
            {
                _active = false;
                FloatingThumb.SetAllVisible(true);
                trace.Finish("finished-before-render");
            }
        }
    }

    // Unique within this session without touching the disk; Util.SaveNewPng
    // moves to the next free name if an older file already has this one.
    private string NextSnipPath()
    {
        string baseName = $"Snip {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        string path = Path.Combine(Util.SnipsDir, baseName + ".png");
        for (int i = 2; _usedPaths.Contains(path); i++)
            path = Path.Combine(Util.SnipsDir, $"{baseName} ({i}).png");
        _usedPaths.Add(path);
        return path;
    }
}
