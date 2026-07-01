using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WinSnipper.Recording;

namespace WinSnipper;

/// <summary>
/// Playback + trim UI for a recording. Scrub to a point, "Set start" /
/// "Set end", then save either a "(trimmed)" copy or replace the original.
/// </summary>
public partial class TrimWindow : Window
{
    private readonly string _path;
    private readonly DispatcherTimer _tick;
    private TimeSpan _duration = TimeSpan.Zero;
    private TimeSpan _trimStart = TimeSpan.Zero;
    private TimeSpan _trimEnd = TimeSpan.Zero;
    private bool _playing;
    private bool _scrubbing;
    private bool _wasPlayingBeforeScrub;
    private bool _updatingSeek;
    private bool _busy;

    // Seeks are throttled: MediaElement decodes from the previous keyframe on
    // every Position set, so seeking per slider pixel makes scrubbing choppy.
    private TimeSpan? _pendingSeek;
    private DateTime _lastSeek = DateTime.MinValue;

    public TrimWindow(string path)
    {
        InitializeComponent();
        _path = path;
        TitleText.Text = $"Trim — {Path.GetFileName(path)}";

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _tick.Tick += (_, _) => SyncFromPlayer();

        Loaded += (_, _) =>
        {
            Player.Source = new Uri(_path);
            // Play/pause once so the first frame renders.
            Player.Play();
            Player.Pause();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            Player.Close();
        };
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Space)
        {
            e.Handled = true;
            TogglePlay();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int round = 2;
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    // ---------- playback ----------

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _trimEnd = _duration;
        Seek.Maximum = Math.Max(1, _duration.TotalMilliseconds);
        _tick.Start();
        UpdateLabels();
        UpdateRangeVisuals();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        _playing = false;
        PlayBtn.Content = "▶";
        Player.Pause();
        Player.Position = TimeSpan.Zero;
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        MessageBox.Show(this, $"Could not play this file: {e.ErrorException?.Message}",
            "WinSnipper", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void Player_Click(object sender, EventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_busy) return;
        _playing = !_playing;
        PlayBtn.Content = _playing ? "⏸" : "▶";
        if (_playing) Player.Play();
        else Player.Pause();
    }

    private void SyncFromPlayer()
    {
        if (_scrubbing)
        {
            ApplyPendingSeek(force: true); // catch up between throttled applies
            return;
        }
        if (_pendingSeek is not null)
        {
            ApplyPendingSeek(force: true);
            return;
        }
        _updatingSeek = true;
        Seek.Value = Player.Position.TotalMilliseconds;
        _updatingSeek = false;
        UpdateLabels();
    }

    private void Seek_DragStarted(object sender, DragStartedEventArgs e)
    {
        _scrubbing = true;
        _wasPlayingBeforeScrub = _playing;
        if (_playing) TogglePlay(); // seeking a playing MediaElement stutters badly
    }

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _scrubbing = false;
        _pendingSeek = TimeSpan.FromMilliseconds(Seek.Value);
        ApplyPendingSeek(force: true);
        if (_wasPlayingBeforeScrub) TogglePlay();
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek) return;
        _pendingSeek = TimeSpan.FromMilliseconds(e.NewValue);
        UpdateLabels();      // label follows the thumb immediately …
        ApplyPendingSeek();  // … the actual decode is rate-limited
    }

    private void ApplyPendingSeek(bool force = false)
    {
        if (_pendingSeek is not { } target) return;
        if (!force && (DateTime.UtcNow - _lastSeek).TotalMilliseconds < 70) return;
        _pendingSeek = null;
        _lastSeek = DateTime.UtcNow;
        Player.Position = target;
    }

    // ---------- trim range ----------

    /// <summary>Where the user currently is: the scrub target if one is in flight.</summary>
    private TimeSpan CurrentPosition => _pendingSeek ?? Player.Position;

    private void SetStart_Click(object sender, RoutedEventArgs e)
    {
        _trimStart = CurrentPosition;
        if (_trimEnd <= _trimStart) _trimEnd = _duration;
        UpdateLabels();
        UpdateRangeVisuals();
    }

    private void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        _trimEnd = CurrentPosition;
        if (_trimStart >= _trimEnd) _trimStart = TimeSpan.Zero;
        UpdateLabels();
        UpdateRangeVisuals();
    }

    private void UpdateLabels()
    {
        TimeLabel.Text = $"{Fmt(CurrentPosition)} / {Fmt(_duration)}";
        RangeLabel.Text = $"{Fmt(_trimStart)} → {Fmt(_trimEnd)}";
        bool trimmed = _trimStart > TimeSpan.Zero || (_duration > TimeSpan.Zero && _trimEnd < _duration);
        SaveBtn.IsEnabled = !_busy && _duration > TimeSpan.Zero && _trimEnd > _trimStart && trimmed;
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss\.f");

    private void RangeCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRangeVisuals();

    private void UpdateRangeVisuals()
    {
        double w = RangeCanvas.ActualWidth;
        if (w <= 0 || _duration <= TimeSpan.Zero) return;
        double x1 = w * (_trimStart.TotalMilliseconds / _duration.TotalMilliseconds);
        double x2 = w * (_trimEnd.TotalMilliseconds / _duration.TotalMilliseconds);
        Canvas.SetLeft(RangeFill, x1);
        RangeFill.Width = Math.Max(0, x2 - x1);
        Canvas.SetLeft(StartMarker, x1 - 1.5);
        Canvas.SetLeft(EndMarker, x2 - 1.5);
    }

    // ---------- save ----------

    private async void SaveTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        bool replace = ReplaceCheck.IsChecked == true;
        SaveBtn.IsEnabled = false;
        TrimProgress.Visibility = Visibility.Visible;
        if (_playing) TogglePlay();

        string finalPath = replace
            ? _path
            : Path.Combine(Path.GetDirectoryName(_path)!,
                Path.GetFileNameWithoutExtension(_path) + " (trimmed).mp4");
        for (int i = 2; !replace && File.Exists(finalPath); i++)
            finalPath = Path.Combine(Path.GetDirectoryName(_path)!,
                Path.GetFileNameWithoutExtension(_path) + $" (trimmed {i}).mp4");
        string tmpPath = finalPath + ".tmp.mp4";

        var start = _trimStart;
        var end = _trimEnd;
        try
        {
            await Task.Run(() => VideoTrimmer.Trim(_path, tmpPath, start, end,
                p => Dispatcher.BeginInvoke(() => TrimProgress.Value = p)));

            if (replace)
            {
                _tick.Stop();
                Player.Close(); // release the file handle before overwriting
            }
            File.Move(tmpPath, finalPath, overwrite: true);

            Process.Start("explorer.exe", $"/select,\"{finalPath}\"");
            Close();
        }
        catch (Exception ex)
        {
            Util.LogCrash("Trim", ex);
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            MessageBox.Show(this, $"Trimming failed: {ex.Message}",
                "WinSnipper", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (replace)
            {
                Player.Source = new Uri(_path); // reopen after the failed replace
                Player.Play(); Player.Pause();
                _tick.Start();
            }
        }
        finally
        {
            _busy = false;
            TrimProgress.Visibility = Visibility.Collapsed;
            UpdateLabels();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
