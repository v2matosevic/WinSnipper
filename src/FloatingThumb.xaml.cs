using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace WinSnipper;

/// <summary>
/// Pinned, draggable thumbnail of a snip. New thumbs stack upward from the
/// bottom-right corner of the monitor the capture came from — not the primary
/// one — so on a multi-display desk the thumbnail lands where you were looking.
/// </summary>
public partial class FloatingThumb : Window
{
    private const double Shadow = 12; // window margin reserved for the drop shadow
    private const double Gap = 16;    // distance from screen edge / between cards

    private static readonly List<FloatingThumb> _open = new();

    private static TimeSpan DismissAfter => TimeSpan.FromSeconds(Settings.Current.DismissSeconds);

    private string _path;
    private readonly bool _isVideo;
    private BitmapSource _img;
    private EditorWindow? _editor;
    private TrimWindow? _trim;
    private readonly DispatcherTimer _dismissTimer;
    private bool _fading;

    // Which monitor this thumb was placed on, and where its top edge sits in
    // physical pixels — stacking has to compare across displays that may run
    // different DPI, so Window.Top (DIPs) is not a usable yardstick.
    private readonly System.Drawing.Point? _anchor;
    private IntPtr _monitor;
    private int _physTop;

    /// <param name="anchor">
    /// A point inside the captured region, in virtual-screen pixels. The thumb
    /// docks to the monitor containing it; null falls back to the cursor's monitor.
    /// </param>
    public FloatingThumb(string path, BitmapSource image, bool isVideo = false,
                         System.Drawing.Point? anchor = null)
    {
        InitializeComponent();
        _path = path;
        _isVideo = isVideo;
        _anchor = anchor;
        Opacity = 0; // AnimateIn fades it up once it is on the right monitor
        _img = image;
        Thumb.Source = image;
        Card.ToolTip = isVideo
            ? $"{IOPath.GetFileName(path)}\nClick to trim · drag into any app to drop the file"
            : $"{IOPath.GetFileName(path)}  ({image.PixelWidth} × {image.PixelHeight})\nClick to edit · drag into any app to drop the file";
        if (!Util.OcrSupported)
            OcrMenuItem.Visibility = Visibility.Collapsed;
        if (isVideo)
        {
            // Image-only actions make no sense for an MP4.
            EditMenuItem.Visibility = Visibility.Collapsed;
            CopyMenuItem.Visibility = Visibility.Collapsed;
            OcrMenuItem.Visibility = Visibility.Collapsed;
            TrimMenuItem.Visibility = Visibility.Visible;
            CopyFileMenuItem.Visibility = Visibility.Visible;
            OpenDefaultMenuItem.Header = "Open in default player";
            PlayBadge.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) =>
        {
            PositionStacked();
            AnimateIn();
        };
        // Dragging the card to another display (or a DPI change on this one)
        // must re-home it, or the next thumb stacks against a stale edge.
        LocationChanged += (_, _) => SyncPhysicalPosition();
        DpiChanged += (_, _) => SyncPhysicalPosition();
        Closed += (_, _) => _open.Remove(this);

        _dismissTimer = new DispatcherTimer { Interval = DismissAfter };
        _dismissTimer.Tick += (_, _) => FadeOutAndClose();
        _dismissTimer.Start();
        // Keep it alive while the context menu is open; resume the countdown after.
        Card.ContextMenuOpening += (_, _) => _dismissTimer.Stop();
        Card.ContextMenu!.Closed += (_, _) => RestartCountdown();
    }

    private bool _pinned;

    // Context menu "Pin" — keep this thumbnail on screen until closed manually.
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = true;
        _dismissTimer.Stop();
    }

    private void RestartCountdown()
    {
        if (_pinned || _fading || _draggingOut) return;
        _dismissTimer.Stop();
        if (!IsMouseOver)
            _dismissTimer.Start();
    }

    private void FadeOutAndClose()
    {
        _dismissTimer.Stop();
        if (_pinned || _fading || _draggingOut) return;
        if (IsMouseOver) return; // MouseLeave restarts the countdown
        _fading = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    public void ShowStacked()
    {
        _open.Add(this);
        Show();
    }

    public static void SetAllVisible(bool visible)
    {
        foreach (var t in _open)
            t.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    private void PositionStacked()
    {
        var wa = _anchor is { } a
            ? Monitors.FromPoint(a.X, a.Y)
            : Monitors.FromForegroundWindow() ?? Monitors.FromCursor();
        _monitor = wa.Handle;

        // ActualWidth/Height are DIPs (DPI-independent); the target monitor's
        // own scale turns them into the physical size it will occupy there.
        double s = wa.Scale;
        int w = (int)Math.Round(ActualWidth * s);
        int h = (int)Math.Round(ActualHeight * s);
        int shadow = (int)Math.Round(Shadow * s);
        int gap = (int)Math.Round(Gap * s);
        int overlap = (int)Math.Round(8 * s);

        int left = wa.Right - w + shadow - gap;

        // Stack above the lowest thumb still on this monitor.
        int bottomEdge = wa.Bottom + shadow - gap;
        foreach (var t in _open)
        {
            if (t == this || t._monitor != wa.Handle) continue;
            bottomEdge = Math.Min(bottomEdge, t._physTop + shadow - overlap);
        }
        int top = bottomEdge - h;
        if (top < wa.Top) top = wa.Top + gap; // screen full of thumbs — just overlap at top

        _physTop = top;
        Monitors.MoveTo(this, left, top, w, h);
    }

    /// <summary>Re-reads the card's real position so stacking stays correct after a drag.</summary>
    private void SyncPhysicalPosition()
    {
        if (Monitors.TopLeftOf(this) is not { } p) return;
        _physTop = p.Top;
        _monitor = Monitors.FromPoint(p.Left, p.Top).Handle;
    }

    private bool _maybeDrag;
    private bool _draggingOut;
    private Point _dragStart;

    // Press-and-move drags the snip out as a real file (Explorer, browsers,
    // upload fields, chats); press-and-release without moving opens the editor.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _maybeDrag = true;
        _dragStart = e.GetPosition(this);
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _maybeDrag = false;
        var data = new DataObject(DataFormats.FileDrop, new[] { _path });
        if (!_isVideo)
            data.SetImage(_img); // for targets that accept bitmaps rather than files
        _draggingOut = true;
        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        finally
        {
            _draggingOut = false;
            RestartCountdown();
        }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_maybeDrag) return;
        _maybeDrag = false;
        OpenEditor(); // a plain click (no movement) opens the editor
    }

    private void AnimateIn()
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
        var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fade);
        EnterTf.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // Round the image corners to match the card.
    private void Thumb_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Thumb.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 7, 7);

    // Opening the editor (or trim window for videos) consumes the thumbnail.
    private void OpenEditor()
    {
        if (_isVideo)
        {
            if (_trim is { IsLoaded: true })
            {
                _trim.Activate();
                return;
            }
            _trim = new TrimWindow(_path);
            _trim.Show();
            _trim.Activate();
            Close();
            return;
        }
        if (_editor is { IsLoaded: true })
        {
            _editor.Activate();
            return;
        }
        _editor = new EditorWindow(_path, _img);
        _editor.Show();
        _editor.Activate();
        Close();
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => OpenEditor();

    private void Copy_Click(object sender, RoutedEventArgs e) => Util.TrySetClipboard(_img);

    private void CopyFile_Click(object sender, RoutedEventArgs e) => Util.TrySetClipboardFile(_path);

    private async void CopyText_Click(object sender, RoutedEventArgs e)
    {
        _pinned = true; // OCR can take a moment — don't fade away mid-work
        _dismissTimer.Stop();
        try
        {
            string? text = await Util.OcrAsync(_img);
            if (!string.IsNullOrWhiteSpace(text))
                Util.TrySetClipboardText(text);
        }
        catch { }
        Close();
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_path); } catch { }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        _pinned = true; // the dialog is modal — don't let the card fade behind it
        _dismissTimer.Stop();

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = IOPath.GetFileName(_path),
            Filter = _isVideo ? "MP4 video|*.mp4" : "PNG image|*.png",
            DefaultExt = _isVideo ? ".mp4" : ".png",
            InitialDirectory = Settings.Current.LastSaveAsDir is { Length: > 0 } d && Directory.Exists(d)
                ? d
                : IOPath.GetDirectoryName(_path),
        };
        if (dlg.ShowDialog() != true) return;

        Settings.Current.LastSaveAsDir = IOPath.GetDirectoryName(dlg.FileName) ?? "";
        Settings.Current.Save();

        if (_isVideo)
        {
            // The MP4 already exists — relocate it and re-point the card, so the
            // next action (drag out, copy file, trim) works on the file the user
            // just chose rather than the one still sitting in Recordings\.
            try
            {
                if (!string.Equals(dlg.FileName, _path, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(_path, dlg.FileName, overwrite: true);
                    try { File.Delete(_path); } catch { /* locked by a player — the copy is what matters */ }
                    _path = dlg.FileName;
                    if (Settings.Current.CopyToClipboard)
                        Util.TrySetClipboardFile(_path);
                }
            }
            catch (Exception ex)
            {
                Util.LogCrash("SaveAsVideo", ex);
                MessageBox.Show(this, $"Could not save there:\n{ex.Message}", "WinSnipper",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            Util.SavePng(_img, dlg.FileName);
        }
    }

    private void OpenDefault_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });

    private void ShowInFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start("explorer.exe", $"/select,\"{_path}\"");

    private void CloseItem_Click(object sender, RoutedEventArgs e) => Close();

    private void Win_MouseEnter(object sender, MouseEventArgs e)
    {
        CloseBtn.Visibility = Visibility.Visible;
        _dismissTimer.Stop();
        if (_fading)
        {
            // caught it mid-fade — restore it; the countdown restarts on leave
            _fading = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }
    }

    private void Win_MouseLeave(object sender, MouseEventArgs e)
    {
        CloseBtn.Visibility = Visibility.Collapsed;
        RestartCountdown();
    }
}
