using System.Diagnostics;
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
/// bottom-right corner of the primary work area.
/// </summary>
public partial class FloatingThumb : Window
{
    private const double Shadow = 12; // window margin reserved for the drop shadow
    private const double Gap = 16;    // distance from screen edge / between cards

    private static readonly List<FloatingThumb> _open = new();

    private static TimeSpan DismissAfter => TimeSpan.FromSeconds(Settings.Current.DismissSeconds);

    private readonly string _path;
    private BitmapSource _img;
    private EditorWindow? _editor;
    private readonly DispatcherTimer _dismissTimer;
    private bool _fading;

    public FloatingThumb(string path, BitmapSource image)
    {
        InitializeComponent();
        _path = path;
        _img = image;
        Thumb.Source = image;
        Card.ToolTip = $"{IOPath.GetFileName(path)}  ({image.PixelWidth} × {image.PixelHeight})\nClick to edit · drag into any app to drop the file";
        Loaded += (_, _) =>
        {
            PositionStacked();
            AnimateIn();
        };
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
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth + Shadow - Gap;

        // Stack above the lowest thumb still on screen.
        double bottomEdge = wa.Bottom + Shadow - Gap;
        foreach (var t in _open)
        {
            if (t == this) continue;
            bottomEdge = Math.Min(bottomEdge, t.Top + Shadow - 8);
        }
        Top = bottomEdge - ActualHeight;
        if (Top < wa.Top) Top = wa.Top + Gap; // screen full of thumbs — just overlap at top
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

    // Opening the editor consumes the thumbnail — it disappears immediately.
    private void OpenEditor()
    {
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
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = IOPath.GetFileName(_path),
            Filter = "PNG image|*.png",
            DefaultExt = ".png",
        };
        if (dlg.ShowDialog() == true)
            Util.SavePng(_img, dlg.FileName);
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
