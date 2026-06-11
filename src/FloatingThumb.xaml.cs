using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
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

    private static readonly TimeSpan DismissAfter = TimeSpan.FromSeconds(4);

    private readonly string _path;
    private BitmapSource _img;
    private bool _userMoved;
    private EditorWindow? _editor;
    private readonly DispatcherTimer _dismissTimer;
    private bool _pinned;
    private bool _fading;

    public FloatingThumb(string path, BitmapSource image)
    {
        InitializeComponent();
        _path = path;
        _img = image;
        Thumb.Source = image;
        Card.ToolTip = $"{IOPath.GetFileName(path)}  ({image.PixelWidth} × {image.PixelHeight})\nDrag into any app to drop the file · double-click to edit";
        Loaded += (_, _) => PositionStacked();
        Closed += (_, _) => _open.Remove(this);

        _dismissTimer = new DispatcherTimer { Interval = DismissAfter };
        _dismissTimer.Tick += (_, _) => FadeOutAndClose();
        _dismissTimer.Start();
        // Keep it alive while the context menu is open; resume the countdown after.
        Card.ContextMenuOpening += (_, _) => _dismissTimer.Stop();
        Card.ContextMenu!.Closed += (_, _) => RestartCountdown();
    }

    /// <summary>Only an open editor keeps the thumbnail around indefinitely.</summary>
    private void Pin()
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
        if (_editor is { IsLoaded: true })
        {
            Pin();
            return;
        }
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

        // Stack above the lowest auto-placed thumb still on screen.
        double bottomEdge = wa.Bottom + Shadow - Gap;
        foreach (var t in _open)
        {
            if (t == this || t._userMoved) continue;
            bottomEdge = Math.Min(bottomEdge, t.Top + Shadow - 8);
        }
        Top = bottomEdge - ActualHeight;
        if (Top < wa.Top) Top = wa.Top + Gap; // screen full of thumbs — just overlap at top
    }

    private bool _maybeDrag;
    private bool _draggingOut;
    private Point _dragStart;

    // Dragging the card drags the snip out as a real file (Explorer, browsers,
    // upload fields, chats). The drag starts only past the system threshold so
    // double-click-to-edit still works.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _maybeDrag = false;
            OpenEditor();
            return;
        }
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

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _maybeDrag = false;

    // The ⠿ grip repositions the thumbnail on screen.
    private void Grip_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _userMoved = true;
        try { DragMove(); } catch { /* released outside a drag */ }
    }

    private void OpenEditor()
    {
        Pin();
        if (_editor is { IsLoaded: true })
        {
            _editor.Activate();
            return;
        }
        _editor = new EditorWindow(_path, _img);
        _editor.ImageSaved += img =>
        {
            _img = img;
            Thumb.Source = img;
        };
        _editor.Show();
        _editor.Activate();
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => OpenEditor();

    private void Copy_Click(object sender, RoutedEventArgs e) => Util.TrySetClipboard(_img);

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
        ToolsBar.Visibility = Visibility.Visible;
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
        ToolsBar.Visibility = Visibility.Collapsed;
        RestartCountdown();
    }
}
