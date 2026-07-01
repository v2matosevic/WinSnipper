using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSnipper.Recording;

namespace WinSnipper;

/// <summary>
/// Player + trim UI: a filmstrip timeline with draggable in/out handles and a
/// playhead (QuickTime-style). Handle/playhead drags never seek per pixel —
/// visuals follow the mouse instantly, the video preview follows on a
/// throttle, so scrubbing stays smooth.
/// </summary>
public partial class TrimWindow : Window
{
    private const int ThumbCount = 14;
    private const double GrabPx = 12; // hit slop around handles/playhead

    private enum DragTarget { None, Start, End, Playhead }

    private readonly string _path;
    private readonly DispatcherTimer _tick;
    private TimeSpan _duration = TimeSpan.Zero;
    private TimeSpan _trimStart = TimeSpan.Zero;
    private TimeSpan _trimEnd = TimeSpan.Zero;
    private TimeSpan _playhead = TimeSpan.Zero;
    private bool _playing;
    private bool _busy;

    private DragTarget _drag = DragTarget.None;
    private bool _wasPlayingBeforeDrag;

    // Seeks are throttled: MediaElement decodes from the previous keyframe on
    // every Position set, so seeking per mouse pixel is what made this choppy.
    private TimeSpan? _pendingSeek;
    private DateTime _lastSeek = DateTime.MinValue;

    public TrimWindow(string path)
    {
        InitializeComponent();
        _path = path;
        TitleText.Text = $"Trim — {Path.GetFileName(path)}";

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => OnTick();

        Loaded += (_, _) =>
        {
            Player.Source = new Uri(_path);
            Player.Play();
            Player.Pause();
            _ = LoadFilmstripAsync();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            Player.Close();
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                TogglePlay();
                break;
            case Key.OemOpenBrackets: // [ — trim start = playhead
                _trimStart = _playhead;
                if (_trimEnd <= _trimStart) _trimEnd = _duration;
                UpdateTimeline();
                break;
            case Key.OemCloseBrackets: // ] — trim end = playhead
                _trimEnd = _playhead;
                if (_trimStart >= _trimEnd) _trimStart = TimeSpan.Zero;
                UpdateTimeline();
                break;
            case Key.Left or Key.Right:
                e.Handled = true;
                double step = (e.Key == Key.Left ? -1 : 1) *
                    (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1000 : 33);
                SeekTo(Clamp(_playhead + TimeSpan.FromMilliseconds(step)), force: true);
                break;
            case Key.Escape:
                if (!_busy) Close();
                break;
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

    // ---------- filmstrip ----------

    private async Task LoadFilmstripAsync()
    {
        try
        {
            var (frames, _) = await Task.Run(() => VideoThumbnails.Extract(_path, ThumbCount, 64));
            foreach (var f in frames)
            {
                FilmStrip.Children.Add(new Image
                {
                    Source = f,
                    Stretch = System.Windows.Media.Stretch.UniformToFill,
                });
            }
        }
        catch (Exception ex)
        {
            Util.LogCrash("Filmstrip", ex); // strip stays dark; trimming still works
        }
    }

    // ---------- playback ----------

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _trimEnd = _duration;
        _tick.Start();
        UpdateTimeline();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        SetPlaying(false);
        SeekTo(_trimStart, force: true);
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
        if (!_playing && (_playhead < _trimStart || _playhead >= _trimEnd))
            SeekTo(_trimStart, force: true); // play previews the selection
        SetPlaying(!_playing);
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayBtn.Content = playing ? "⏸" : "▶";
        if (playing) Player.Play();
        else Player.Pause();
    }

    private void OnTick()
    {
        if (_drag == DragTarget.None)
        {
            ApplyPendingSeek(force: true); // catch up between throttled applies
            if (_pendingSeek is null)
                _playhead = Player.Position;
        }
        else
        {
            ApplyPendingSeek(force: true);
        }

        // Playing past the trim end previews exactly what the export keeps.
        if (_playing && _trimEnd > TimeSpan.Zero && _playhead >= _trimEnd)
        {
            SetPlaying(false);
            SeekTo(_trimEnd, force: true);
        }
        UpdateTimeline();
    }

    private void SeekTo(TimeSpan t, bool force = false)
    {
        _playhead = Clamp(t);
        _pendingSeek = _playhead;
        ApplyPendingSeek(force);
    }

    private void ApplyPendingSeek(bool force = false)
    {
        if (_pendingSeek is not { } target) return;
        if (!force && (DateTime.UtcNow - _lastSeek).TotalMilliseconds < 90) return;
        _pendingSeek = null;
        _lastSeek = DateTime.UtcNow;
        Player.Position = target;
    }

    private TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.Zero ? TimeSpan.Zero : t > _duration ? _duration : t;

    // ---------- timeline interaction ----------

    private double TimelineWidth => TimelineHost.ActualWidth;

    private double XOf(TimeSpan t) =>
        _duration > TimeSpan.Zero ? TimelineWidth * t.Ticks / _duration.Ticks : 0;

    private TimeSpan TimeAt(double x) =>
        _duration > TimeSpan.Zero && TimelineWidth > 0
            ? new TimeSpan((long)(_duration.Ticks * Math.Clamp(x / TimelineWidth, 0, 1)))
            : TimeSpan.Zero;

    private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _duration <= TimeSpan.Zero) return;
        double x = e.GetPosition(TimelineHost).X;
        double xL = XOf(_trimStart), xR = XOf(_trimEnd);

        _drag = Math.Abs(x - xL) <= GrabPx && Math.Abs(x - xL) <= Math.Abs(x - xR) ? DragTarget.Start
              : Math.Abs(x - xR) <= GrabPx ? DragTarget.End
              : DragTarget.Playhead;

        _wasPlayingBeforeDrag = _playing;
        if (_playing) SetPlaying(false); // scrub paused, resume on release
        TimelineHost.CaptureMouse();
        Timeline_MouseMove(sender, e);
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_busy) return;
        double x = e.GetPosition(TimelineHost).X;

        if (_drag == DragTarget.None)
        {
            // Cursor affordance when hovering a handle.
            double xL = XOf(_trimStart), xR = XOf(_trimEnd);
            TimelineHost.Cursor = Math.Abs(x - xL) <= GrabPx || Math.Abs(x - xR) <= GrabPx
                ? Cursors.SizeWE
                : Cursors.Arrow;
            return;
        }

        var t = TimeAt(x);
        switch (_drag)
        {
            case DragTarget.Start:
                _trimStart = t < _trimEnd ? t : _trimEnd;
                _playhead = _trimStart;
                break;
            case DragTarget.End:
                _trimEnd = t > _trimStart ? t : _trimStart;
                _playhead = _trimEnd;
                break;
            case DragTarget.Playhead:
                _playhead = t;
                break;
        }
        _pendingSeek = _playhead;
        ApplyPendingSeek(); // visuals are instant, the decode is rate-limited
        UpdateTimeline();
    }

    private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragTarget.None) return;
        bool resumePlaying = _wasPlayingBeforeDrag && _drag == DragTarget.Playhead;
        _drag = DragTarget.None;
        TimelineHost.ReleaseMouseCapture();
        ApplyPendingSeek(force: true);
        if (resumePlaying) SetPlaying(true);
    }

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimeline();

    private void UpdateTimeline()
    {
        double w = TimelineWidth;
        double h = TimelineHost.ActualHeight;
        if (w <= 0 || _duration <= TimeSpan.Zero) return;

        double xL = XOf(_trimStart), xR = XOf(_trimEnd), xP = XOf(_playhead);

        DimL.Height = h;
        DimR.Height = h;
        Canvas.SetLeft(DimL, 0);
        DimL.Width = Math.Max(0, xL);
        Canvas.SetLeft(DimR, xR);
        DimR.Width = Math.Max(0, w - xR);

        SelFrame.Height = h;
        Canvas.SetLeft(SelFrame, xL);
        SelFrame.Width = Math.Max(0, xR - xL);

        HandleL.Height = h;
        HandleR.Height = h;
        Canvas.SetLeft(HandleL, Math.Max(0, xL - HandleL.Width + 2));
        Canvas.SetLeft(HandleR, Math.Min(w - 2, xR - 2));

        Playhead.Height = h;
        Canvas.SetLeft(Playhead, xP - 1);
        Canvas.SetLeft(PlayheadKnob, xP - PlayheadKnob.Width / 2);

        TimeLabel.Text = $"{Fmt(_playhead)} / {Fmt(_duration)}";
        var selected = _trimEnd - _trimStart;
        RangeLabel.Text = $"{Fmt(_trimStart)} — {Fmt(_trimEnd)}   ·   {selected.TotalSeconds:0.0}s selected";

        bool trimmed = _trimStart > TimeSpan.Zero || _trimEnd < _duration;
        SaveBtn.IsEnabled = !_busy && _trimEnd > _trimStart && trimmed;
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss\.f");

    // ---------- save ----------

    private async void SaveTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        bool replace = ReplaceCheck.IsChecked == true;
        SaveBtn.IsEnabled = false;
        TrimProgress.Visibility = Visibility.Visible;
        if (_playing) SetPlaying(false);

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
            UpdateTimeline();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
