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
    /// <summary>Upper bound on decoded preview frames — a very wide window shouldn't decode forever.</summary>
    private const int MaxThumbs = 48;
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

    // Filmstrip: tiles are sized to the video's own aspect ratio and as many
    // are laid down as the timeline is wide, so the strip reads like film
    // rather than a row of centre-cropped squares.
    private double _stripAspect;
    private int _stripCount = -1;
    private System.Threading.CancellationTokenSource? _stripCts;
    private DispatcherTimer? _stripDebounce;

    public TrimWindow(string path)
    {
        InitializeComponent();
        DarkWindow.Attach(this, Root);
        _path = path;
        TitleText.Text = Path.GetFileName(path);

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => OnTick();

        Loaded += (_, _) =>
        {
            Player.Source = new Uri(_path);
            Player.Play();
            Player.Pause();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            _stripDebounce?.Stop();
            _stripCts?.Cancel();
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

    // ---------- filmstrip ----------

    /// <summary>
    /// Rebuilds the strip for the current timeline width. Tile width is fixed
    /// by the video's aspect, so resizing changes how many frames are shown,
    /// not how squashed each one is.
    /// </summary>
    private async void RebuildFilmstripAsync()
    {
        double stripH = TimelineHost.ActualHeight;
        double stripW = TimelineWidth;
        if (stripH <= 0 || stripW <= 0 || _stripAspect <= 0) return;

        double tileW = Math.Max(24, stripH * _stripAspect);
        int count = Math.Clamp((int)Math.Ceiling(stripW / tileW), 1, MaxThumbs);
        if (count == _stripCount) return; // same strip — don't re-decode on every drag pixel
        _stripCount = count;

        _stripCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _stripCts = cts;

        try
        {
            // Decode at the strip's real device height so tiles stay crisp on a HiDPI display.
            int px = Math.Max(32, (int)Math.Round(stripH * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY));
            var (frames, _) = await Task.Run(() => VideoThumbnails.Extract(_path, count, px), cts.Token);
            if (cts.IsCancellationRequested || !IsLoaded) return;

            FilmStrip.Children.Clear();
            foreach (var f in frames)
            {
                FilmStrip.Children.Add(new Image
                {
                    Source = f,
                    Width = tileW,
                    Height = stripH,
                    Stretch = System.Windows.Media.Stretch.Fill,
                });
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer resize
        }
        catch (Exception ex)
        {
            Util.LogCrash("Filmstrip", ex); // strip stays dark; trimming still works
        }
    }

    /// <summary>Coalesces the burst of SizeChanged events a window drag produces.</summary>
    private void QueueFilmstripRebuild()
    {
        _stripDebounce ??= CreateStripDebounce();
        _stripDebounce.Stop();
        _stripDebounce.Start();
    }

    private DispatcherTimer CreateStripDebounce()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        t.Tick += (_, _) => { t.Stop(); RebuildFilmstripAsync(); };
        return t;
    }

    // ---------- playback ----------

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _trimEnd = _duration;
        _tick.Start();
        UpdateTimeline();

        // MediaElement already knows the frame size — no need to decode a probe
        // frame just to learn the aspect ratio.
        _stripAspect = Player.NaturalVideoHeight > 0
            ? Player.NaturalVideoWidth / (double)Player.NaturalVideoHeight
            : 16.0 / 9.0;
        _stripCount = -1;
        RebuildFilmstripAsync();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        SetPlaying(false);
        SeekTo(_trimStart, force: true);
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ErrorOverlayText.Text = $"Could not play this file.\n{e.ErrorException?.Message}";
        ErrorOverlay.Visibility = Visibility.Visible;
        UpdatePlayOverlay();
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
        if (playing) _hasPlayed = true;
        PlayBtn.Content = FindResource(playing ? "Ico.Pause" : "Ico.Play");
        UpdatePlayOverlay();
        if (playing) Player.Play();
        else Player.Pause();
    }

    private bool _hasPlayed;

    // The big play button invites the first click; after that it only shows
    // while hovering the paused video, so it never sits on the frame you are
    // lining a cut up against.
    private void UpdatePlayOverlay() =>
        PlayOverlay.Visibility = !_playing && ErrorOverlay.Visibility != Visibility.Visible
                                 && (!_hasPlayed || PlayerFrame.IsMouseOver)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void PlayerFrame_Hover(object sender, MouseEventArgs e) => UpdatePlayOverlay();

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

    /// <summary>Handle width. Handles sit inside the kept range, QuickTime-style.</summary>
    private const double HandleW = 14;
    private const double PlayheadOverhang = 5;

    private double _grabOffset; // cursor-to-edge distance at mouse down, so a handle doesn't jump

    private double TimelineWidth => TimelineHost.ActualWidth;

    /// <summary>Top of the filmstrip inside the (taller) grab area.</summary>
    private double StripTop => (TimelineArea.ActualHeight - TimelineHost.ActualHeight) / 2;

    private double XOf(TimeSpan t) =>
        _duration > TimeSpan.Zero ? TimelineWidth * t.Ticks / _duration.Ticks : 0;

    private TimeSpan TimeAt(double x) =>
        _duration > TimeSpan.Zero && TimelineWidth > 0
            ? new TimeSpan((long)(_duration.Ticks * Math.Clamp(x / TimelineWidth, 0, 1)))
            : TimeSpan.Zero;

    /// <summary>The handle under x, if any; each one grabs around its own centre.</summary>
    private DragTarget HandleAt(double x)
    {
        double dL = Math.Abs(x - (XOf(_trimStart) + HandleW / 2));
        double dR = Math.Abs(x - (XOf(_trimEnd) - HandleW / 2));
        if (dL <= GrabPx && dL <= dR) return DragTarget.Start;
        if (dR <= GrabPx) return DragTarget.End;
        return DragTarget.None;
    }

    private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _duration <= TimeSpan.Zero) return;
        double x = e.GetPosition(TimelineHost).X;

        _drag = HandleAt(x);
        if (_drag == DragTarget.None) _drag = DragTarget.Playhead;
        _grabOffset = _drag switch
        {
            DragTarget.Start => x - XOf(_trimStart),
            DragTarget.End => x - XOf(_trimEnd),
            _ => 0,
        };

        _wasPlayingBeforeDrag = _playing;
        if (_playing) SetPlaying(false); // scrub paused, resume on release
        TimelineArea.CaptureMouse();
        Timeline_MouseMove(sender, e);
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_busy) return;
        double x = e.GetPosition(TimelineHost).X;

        if (_drag == DragTarget.None)
        {
            // Cursor affordance when hovering a handle.
            TimelineArea.Cursor = HandleAt(x) != DragTarget.None ? Cursors.SizeWE : Cursors.Arrow;
            return;
        }

        var t = TimeAt(x - _grabOffset);
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
        ShowTimeBadge(_drag switch
        {
            DragTarget.Start => _trimStart,
            DragTarget.End => _trimEnd,
            _ => _playhead,
        });
        UpdateTimeline();
    }

    private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragTarget.None) return;
        bool resumePlaying = _wasPlayingBeforeDrag && _drag == DragTarget.Playhead;
        _drag = DragTarget.None;
        TimeBadge.Visibility = Visibility.Collapsed;
        TimelineArea.ReleaseMouseCapture();
        ApplyPendingSeek(force: true);
        if (resumePlaying) SetPlaying(true);
    }

    /// <summary>Small time bubble above the strip that follows whatever is being dragged.</summary>
    private void ShowTimeBadge(TimeSpan t)
    {
        TimeBadgeText.Text = Fmt(t);
        TimeBadge.Visibility = Visibility.Visible;
        TimeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = TimeBadge.DesiredSize.Width;
        double x = Math.Clamp(XOf(t) - bw / 2, 0, Math.Max(0, TimelineWidth - bw));
        Canvas.SetLeft(TimeBadge, x);
        Canvas.SetTop(TimeBadge, StripTop - PlayheadOverhang - TimeBadge.DesiredSize.Height - 6);
    }

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Border.CornerRadius doesn't clip children; do it explicitly.
        TimelineInner.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, TimelineHost.ActualWidth, TimelineHost.ActualHeight), 8, 8);
        UpdateTimeline();
        QueueFilmstripRebuild();
    }

    private void PlayerFrame_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PlayerInner.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, PlayerFrame.ActualWidth, PlayerFrame.ActualHeight), 10, 10);

    // Shortcut hints are a courtesy: drop them whole rather than clip a key in half.
    private void Footer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHints();

    private void UpdateHints() =>
        Hints.Visibility = ErrorText.Visibility != Visibility.Visible
                           && Hints.DesiredSize.Width <= Footer.ColumnDefinitions[0].ActualWidth
            ? Visibility.Visible
            : Visibility.Hidden;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.ToolTip = message;
        ErrorText.Visibility = Visibility.Visible;
        UpdateHints();
    }

    private void UpdateTimeline()
    {
        double w = TimelineWidth;
        double h = TimelineHost.ActualHeight;
        if (w <= 0 || _duration <= TimeSpan.Zero) return;

        double top = StripTop;
        double xL = XOf(_trimStart), xR = XOf(_trimEnd), xP = XOf(_playhead);

        DimL.Height = h;
        DimR.Height = h;
        Canvas.SetLeft(DimL, 0);
        DimL.Width = Math.Max(0, xL);
        Canvas.SetLeft(DimR, xR);
        DimR.Width = Math.Max(0, w - xR);

        Canvas.SetTop(SelFrame, top);
        SelFrame.Height = h;
        Canvas.SetLeft(SelFrame, xL);
        SelFrame.Width = Math.Max(0, xR - xL);

        Canvas.SetTop(HandleL, top);
        Canvas.SetTop(HandleR, top);
        HandleL.Height = h;
        HandleR.Height = h;
        Canvas.SetLeft(HandleL, xL);
        Canvas.SetLeft(HandleR, xR - HandleW);

        Playhead.Height = h + PlayheadOverhang * 2;
        Canvas.SetTop(Playhead, top - PlayheadOverhang);
        Canvas.SetLeft(Playhead, xP - 1);
        Canvas.SetTop(PlayheadKnob, top - PlayheadOverhang - PlayheadKnob.Height / 2 + 1);
        Canvas.SetLeft(PlayheadKnob, xP - PlayheadKnob.Width / 2);

        TimeNow.Text = Fmt(_playhead);
        TimeTotal.Text = Fmt(_duration);
        var selected = _trimEnd - _trimStart;
        bool trimmed = _trimStart > TimeSpan.Zero || _trimEnd < _duration;
        RangeLabel.Text = trimmed
            ? $"Keeping {Fmt(_trimStart)} – {Fmt(_trimEnd)}   ·   {selected.TotalSeconds:0.0} s"
            : $"Full clip   ·   {selected.TotalSeconds:0.0} s   ·   drag the handles to trim";

        bool valid = _trimEnd > _trimStart;
        // While saving, the primary button stays lit because it is showing the
        // progress; _busy is what blocks a second save.
        SaveBtn.IsEnabled = _busy || (valid && trimmed);
        SaveAsBtn.IsEnabled = !_busy && valid; // exporting an untrimmed copy elsewhere is still useful
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss\.f");

    // ---------- save ----------

    private void SaveTrim_Click(object sender, RoutedEventArgs e) => _ = SaveAsync(askWhere: false);

    /// <summary>Same trim, but the destination is chosen in a file dialog.</summary>
    private void SaveTrimAs_Click(object sender, RoutedEventArgs e) => _ = SaveAsync(askWhere: true);

    private async Task SaveAsync(bool askWhere)
    {
        if (_busy) return;
        bool replace = !askWhere && ReplaceCheck.IsChecked == true;

        string finalPath;
        if (replace)
        {
            finalPath = _path;
        }
        else
        {
            finalPath = NextTrimmedPath();
            if (askWhere)
            {
                string? lastDir = Settings.Current.LastSaveAsDir;
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save trimmed recording",
                    FileName = Path.GetFileName(finalPath),
                    Filter = "MP4 video|*.mp4",
                    DefaultExt = ".mp4",
                    InitialDirectory = !string.IsNullOrEmpty(lastDir) && Directory.Exists(lastDir)
                        ? lastDir
                        : Path.GetDirectoryName(_path),
                };
                if (dlg.ShowDialog(this) != true) return;

                finalPath = dlg.FileName;
                Settings.Current.LastSaveAsDir = Path.GetDirectoryName(finalPath) ?? "";
                Settings.Current.Save();

                // Overwriting the file we are reading from would deadlock the
                // reader; treat "save as, onto myself" as a replace.
                replace = string.Equals(finalPath, _path, StringComparison.OrdinalIgnoreCase);
            }
        }

        _busy = true;
        SaveAsBtn.IsEnabled = false;
        SaveBtn.Content = "Saving…";
        TrimProgress.Value = 0;
        TrimProgress.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateHints();
        if (_playing) SetPlaying(false);

        string tmpPath = finalPath + ".tmp.mp4";

        var start = _trimStart;
        var end = _trimEnd;
        try
        {
            await Task.Run(() => VideoTrimmer.Trim(_path, tmpPath, start, end,
                p => Dispatcher.BeginInvoke(() =>
                {
                    TrimProgress.Value = p;
                    SaveBtn.Content = $"Saving {p * 100:0}%";
                })));

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
            ShowError($"Trimming failed: {ex.Message}");
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
            SaveBtn.Content = "Save trimmed";
            TrimProgress.Visibility = Visibility.Collapsed;
            UpdateTimeline();
        }
    }

    /// <summary>"clip (trimmed).mp4" next to the source, skipping names already taken.</summary>
    private string NextTrimmedPath()
    {
        string dir = Path.GetDirectoryName(_path)!;
        string stem = Path.GetFileNameWithoutExtension(_path);
        string path = Path.Combine(dir, stem + " (trimmed).mp4");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{stem} (trimmed {i}).mp4");
        return path;
    }
}
