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
    private TaskCompletionSource? _stopCompletion;
    private SnipOverlay? _prepared;
    private readonly Func<PerformanceTrace, Task<(System.Windows.Media.Imaging.BitmapSource, Int32Rect)>> _capture;
    private readonly Func<SnipOverlay, bool> _select;

    public RecordingManager() : this(trace => CaptureWorker.CaptureAsync(trace), overlay => overlay.ShowScreenshotSelection()) { }
    internal RecordingManager(Func<PerformanceTrace, Task<(System.Windows.Media.Imaging.BitmapSource, Int32Rect)>> capture,
        Func<SnipOverlay, bool> select) { _capture = capture; _select = select; }

    public bool IsRecording => _recorder is not null;
    public bool IsPaused => _recorder?.IsPaused == true;
    public event Action? StateChanged;

    public Action<string>? OnError;
    public Action<string>? OnInfo;

    public void Toggle() => Toggle(new PerformanceTrace("record-menu"));

    public void Toggle(PerformanceTrace trace) => _ = ToggleAsync(trace);

    public async Task ToggleAsync(PerformanceTrace trace)
    {
        if (_recorder is not null)
        {
            trace.Finish("stop-requested");
            await StopAsync();
            return;
        }
        await StartNewAsync(trace);
    }

    public void PrepareOverlay()
    {
        if (_selecting || SnipOverlay.IsOpen || _prepared is { IsClosed: false }) return;
        try
        {
            _prepared = SnipOverlay.PrepareScreenshot();
            _prepared.SetRecordingSelection();
        }
        catch (Exception ex) { Util.LogCrash("Prepare recording selector", ex); }
    }

    private async Task StartNewAsync(PerformanceTrace trace)
    {
        if (_selecting || SnipOverlay.IsOpen) { trace.Finish("already-open"); return; }
        using var reservation = SnipOverlay.TryReserveCapture();
        if (reservation is null) { trace.Finish("already-open"); return; }
        _selecting = true;
        SnipOverlay? overlay = null;
        try
        {
            // An opaque frozen preview avoids a layered, virtual-desktop-sized
            // transparency upload on every pointer move while a game is rendering.
            FloatingThumb.SetAllVisible(false);
            var (screenshot, bounds) = await _capture(trace);
            if (System.Windows.Application.Current.Dispatcher.HasShutdownStarted) return;
            overlay = _prepared is { IsClosed: false } ? _prepared : SnipOverlay.PrepareScreenshot();
            _prepared = overlay;
            overlay.SetScreenshot(screenshot, bounds);
            overlay.SetRecordingSelection();
            trace.TrackWindow(overlay);
            bool ok = _select(overlay);
            var selection = overlay.SelectionPx; // ClearScreenshot releases the cached selection as well as its pixels.
            overlay.ClearScreenshot();
            FloatingThumb.SetAllVisible(true);

            if (!ok || selection is not { Width: > 0, Height: > 0 } sel)
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
            StateChanged?.Invoke();
            recorder.CaptureModeChanged += supportsExclusion =>
            {
                // Clear native affinity synchronously, without waiting on WPF.
                // UI status is posted separately so stopping cannot deadlock.
                if (!supportsExclusion) _hud?.PrepareCompatibilityCapture();
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                if (!dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (_recorder != recorder) return;
                        _hud?.SetCaptureMode(supportsExclusion);
                        if (_hud?.ControlsMayBeRecorded == true)
                            OnInfo?.Invoke("Capture switched to compatibility mode. The recording controls may now appear in the video.");
                    }));
            };
            await recorder.CaptureReady;
            if (_recorder != recorder || _stopping) return;
            if (recorder.Error is not null) { await StopAsync(); return; }

            _hud = new RecordingHud(region, () => recorder.Elapsed, recorder.ExcludesControlWindows,
                () => recorder.ExcludesControlWindows);
            _hud.StopRequested += () => _ = StopAsync();
            _hud.PauseToggled += SetPaused;
            _hud.SetPaused(recorder.IsPaused);
            _hud.Show();
            OnInfo?.Invoke(_hud.ControlsMayBeRecorded
                ? "Recording. Controls may appear in this compatibility capture. Ctrl+Shift+F12 stops; Ctrl+Shift+F10 pauses."
                : "Recording. Ctrl+Shift+F12 stops; Ctrl+Shift+F10 pauses.");

            var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            guard.Tick += (_, _) =>
            {
                if (_recorder != recorder) { guard.Stop(); return; }
                if (recorder.Error is not null)
                {
                    guard.Stop();
                    _ = StopAsync();
                }
                else if (recorder.Elapsed > MaxDuration)
                {
                    guard.Stop();
                    _ = StopAsync();
                }
            };
            guard.Start();
        }
        catch (Exception ex)
        {
            Util.LogCrash("Recording selection/start", ex);
            if (_recorder is not null) await StopAsync();
            OnError?.Invoke($"Could not start recording: {ex.Message}");
        }
        finally
        {
            overlay?.ClearScreenshot();
            FloatingThumb.SetAllVisible(true);
            trace.Finish("finished-before-render");
            _selecting = false;
        }
    }

    public void TogglePause() => SetPaused(!IsPaused);

    private void SetPaused(bool paused)
    {
        if (_recorder is null || _stopping) return;
        _recorder.SetPaused(paused);
        _hud?.SetPaused(paused);
        StateChanged?.Invoke();
    }

    public void ShowControls() => _hud?.Reveal();

    public async Task StopAsync()
    {
        if (_stopping)
        {
            if (_stopCompletion is not null) await _stopCompletion.Task;
            return;
        }
        if (_recorder is null) return;
        _stopping = true;
        var completion = _stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = _recorder;
        try
        {
            _hud?.Close();
            _hud = null;

            var lastFrame = await recorder.StopAsync();
            if (recorder.Error is not null)
            {
                OnError?.Invoke($"Recording could not be completed: {recorder.Error.Message}\nAny partial file remains at {recorder.FilePath}");
                return;
            }
            if (lastFrame is null || !File.Exists(recorder.FilePath))
            {
                OnError?.Invoke("No usable recording was produced.");
                return;
            }

            string path = Settings.Current.AskWhereToSaveRecordings
                ? await PromptForDestinationAsync(recorder.FilePath)
                : recorder.FilePath;

            var copying = Settings.Current.CopyToClipboard ? Util.SetClipboardFileAsync(path) : null;

            var r = recorder.Region;
            var anchor = new System.Drawing.Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            new FloatingThumb(path, lastFrame, isVideo: true, anchor: anchor, copying: copying).ShowStacked();
        }
        catch (Exception ex)
        {
            Util.LogCrash("RecordingStop", ex);
            OnError?.Invoke($"Recording could not be completed: {ex.Message}\nCheck {recorder.FilePath}");
        }
        finally
        {
            _recorder = null;
            _stopping = false;
            StateChanged?.Invoke();
            completion.TrySetResult();
        }
    }

    /// <summary>
    /// Asks where the finished recording should live. The MP4 is already on
    /// disk under Recordings\ — cancelling simply keeps it there, so a stray
    /// Escape can never lose a take.
    /// </summary>
    private async Task<string> PromptForDestinationAsync(string current)
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

            await Task.Run(() => ReliableIO.Write(dlg.FileName, output =>
            {
                using var input = File.OpenRead(current);
                input.CopyTo(output);
            }));
            try { File.Delete(current); } catch { /* leave the original if it is still locked */ }

            Settings.Current.LastSaveAsDir = Path.GetDirectoryName(dlg.FileName) ?? "";
            try { Settings.Current.Save(); } catch (Exception ex) { Util.LogCrash("RecordingSaveAsSettings", ex); }
            return dlg.FileName;
        }
        catch (Exception ex)
        {
            Util.LogCrash("RecordingSaveAs", ex);
            OnError?.Invoke($"Could not save to that destination. Recording remains at {current}\n{ex.Message}");
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
