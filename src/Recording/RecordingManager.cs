using System.IO;
using System.Windows;

namespace WinSnipper.Recording;

/// <summary>
/// Orchestrates one screen recording: region selection (same overlay as
/// snips) → capture to MP4 with a floating HUD → floating thumbnail that
/// opens the trim window. The hotkey toggles: first press selects and starts,
/// second press stops.
/// </summary>
public sealed class RecordingManager
{
    /// <summary>Recordings longer than this stop themselves (runaway guard).</summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(3);

    private bool _selecting;
    private ScreenRecorder? _recorder;
    private RecordingHud? _hud;
    private bool _stopping;

    public bool IsRecording => _recorder is not null;

    public Action<string>? OnError;
    public Action<string>? OnInfo;

    public void Toggle()
    {
        if (_recorder is not null)
        {
            _ = StopAsync();
            return;
        }
        StartNew();
    }

    private void StartNew()
    {
        if (_selecting || SnipOverlay.IsOpen) return;
        _selecting = true;
        try
        {
            // Live overlay — no frozen screenshot, the desktop keeps moving.
            var bounds = ScreenCapture.VirtualScreenBounds();
            var overlay = new SnipOverlay(bounds);
            bool? ok = overlay.ShowDialog();

            if (ok != true || overlay.SelectionPx is not { Width: > 0, Height: > 0 } sel)
                return;

            // Selection is relative to the captured bitmap; recording needs
            // absolute screen coordinates. H.264 wants even dimensions.
            var region = new Int32Rect(sel.X + bounds.X, sel.Y + bounds.Y, sel.Width & ~1, sel.Height & ~1);
            if (region.Width < 16 || region.Height < 16)
            {
                OnError?.Invoke("Selection too small to record — drag a larger region.");
                return;
            }

            string path = NextRecordingPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var recorder = new ScreenRecorder(path, region, Settings.Current.RecordFps, Settings.Current.RecordCursor);
            try
            {
                recorder.Start();
            }
            catch (Exception ex)
            {
                Util.LogCrash("RecordingStart", ex);
                OnError?.Invoke($"Could not start recording: {ex.Message}");
                return;
            }
            _recorder = recorder;

            _hud = new RecordingHud(region, () => recorder.Elapsed);
            _hud.StopRequested += () => _ = StopAsync();
            _hud.PauseToggled += paused => recorder.SetPaused(paused);
            _hud.Show();
            if (_hud.PillHidden)
                OnInfo?.Invoke($"Recording… press {Util.RecordHotkeyDisplay} to stop.");

            var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            guard.Tick += (_, _) =>
            {
                if (_recorder != recorder) { guard.Stop(); return; }
                if (recorder.Error is not null)
                {
                    guard.Stop();
                    _ = StopAsync();
                    OnError?.Invoke($"Recording failed: {recorder.Error.Message}");
                }
                else if (recorder.Elapsed > MaxDuration)
                {
                    guard.Stop();
                    _ = StopAsync();
                }
            };
            guard.Start();
        }
        finally
        {
            _selecting = false;
        }
    }

    public async Task StopAsync()
    {
        if (_recorder is null || _stopping) return;
        _stopping = true;
        var recorder = _recorder;
        try
        {
            _hud?.Close();
            _hud = null;

            var lastFrame = await recorder.StopAsync();
            if (lastFrame is null || !File.Exists(recorder.FilePath))
                return; // nothing captured (or the recorder already reported the error)

            string path = Settings.Current.AskWhereToSaveRecordings
                ? PromptForDestination(recorder.FilePath)
                : recorder.FilePath;

            if (Settings.Current.CopyToClipboard)
                Util.TrySetClipboardFile(path);

            var r = recorder.Region;
            var anchor = new System.Drawing.Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            new FloatingThumb(path, lastFrame, isVideo: true, anchor: anchor).ShowStacked();
        }
        finally
        {
            _recorder = null;
            _stopping = false;
        }
    }

    /// <summary>
    /// Asks where the finished recording should live. The MP4 is already on
    /// disk under Recordings\ — cancelling simply keeps it there, so a stray
    /// Escape can never lose a take.
    /// </summary>
    private static string PromptForDestination(string current)
    {
        try
        {
            string? lastDir = Settings.Current.LastSaveAsDir;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save recording",
                FileName = Path.GetFileName(current),
                Filter = "MP4 video|*.mp4",
                DefaultExt = ".mp4",
                InitialDirectory = !string.IsNullOrEmpty(lastDir) && Directory.Exists(lastDir)
                    ? lastDir
                    : Util.RecordingsDir,
            };
            if (dlg.ShowDialog() != true) return current;
            if (string.Equals(dlg.FileName, current, StringComparison.OrdinalIgnoreCase)) return current;

            Directory.CreateDirectory(Path.GetDirectoryName(dlg.FileName)!);
            File.Copy(current, dlg.FileName, overwrite: true);
            try { File.Delete(current); } catch { /* leave the original if it is still locked */ }

            Settings.Current.LastSaveAsDir = Path.GetDirectoryName(dlg.FileName) ?? "";
            Settings.Current.Save();
            return dlg.FileName;
        }
        catch (Exception ex)
        {
            Util.LogCrash("RecordingSaveAs", ex);
            return current; // the recording is safe where it is
        }
    }

    private static string NextRecordingPath()
    {
        string baseName = $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        string path = Path.Combine(Util.RecordingsDir, baseName + ".mp4");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(Util.RecordingsDir, $"{baseName} ({i}).mp4");
        return path;
    }
}
