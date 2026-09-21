using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinSnipper;

/// <summary>Orchestrates one snip: capture → overlay selection → save + clipboard → floating thumbnail.</summary>
public sealed class SnipManager
{
    private bool _active;
    private SnipOverlay? _prepared;

    public void PrepareOverlay()
    {
        if (_active || SnipOverlay.IsOpen || _prepared is not null) return;
        try { _prepared = SnipOverlay.PrepareScreenshot(); }
        catch (Exception ex) { Util.LogCrash("Prepare overlay", ex); }
    }

    public void StartSnip() => StartSnip(new PerformanceTrace("snip-menu"));

    public void StartSnip(PerformanceTrace trace)
    {
        if (_active || SnipOverlay.IsOpen) { trace.Finish("already-open"); return; }
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
                (shot, bounds) = ScreenCapture.CaptureVirtualScreen();
                trace.Mark("captured");
            }
            catch (Exception ex)
            {
                trace.Finish("capture-failed");
                Util.LogCrash("Screenshot capture", ex);
                throw;
            }

            overlay = _prepared ?? SnipOverlay.PrepareScreenshot();
            _prepared = null;
            overlay.SetScreenshot(shot, bounds);
            trace.TrackWindow(overlay);
            bool? ok = overlay.ShowDialog();
            FloatingThumb.SetAllVisible(true);

            if (ok == true && overlay.SelectionPx is { Width: > 0, Height: > 0 } sel)
            {
                var cropped = new CroppedBitmap(shot, sel);
                cropped.Freeze();

                string path = NextSnipPath();
                // Encode and write on a worker. PNG compression of a
                // full-screen selection is long enough to be felt, and nothing
                // about it needs to happen before the thumbnail appears —
                // anything that does need the file waits on this task first.
                var saving = Task.Run(() => Util.SavePng(cropped, path));

                if (Settings.Current.CopyToClipboard)
                    Util.TrySetClipboard(cropped);

                // Selection is relative to the captured bitmap; the thumbnail
                // docks to whichever monitor the selection's centre falls on.
                var anchor = new System.Drawing.Point(
                    bounds.X + sel.X + sel.Width / 2,
                    bounds.Y + sel.Y + sel.Height / 2);
                new FloatingThumb(path, cropped, anchor: anchor, saving: saving).ShowStacked();
            }
        }
        finally
        {
            try { overlay?.Close(); }
            finally
            {
                _active = false;
                FloatingThumb.SetAllVisible(true);
                trace.Finish("finished-before-render");
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle, (Action)PrepareOverlay);
            }
        }
    }

    private static string NextSnipPath()
    {
        string baseName = $"Snip {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        string path = Path.Combine(Util.SnipsDir, baseName + ".png");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(Util.SnipsDir, $"{baseName} ({i}).png");
        return path;
    }
}
