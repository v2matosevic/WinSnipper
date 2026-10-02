using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinSnipper;

public partial class HistoryWindow : Window
{
    private string _saveDir;
    private bool _followSettings;
    private bool _closed, _refreshing, _refreshAgain, _acting, _applyingFilter;
    private List<CaptureEntry> _entries = new();
    private string _filter = "all";
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private int _previewRevision;
    private readonly Dictionary<string, Window> _editors = new(StringComparer.OrdinalIgnoreCase);

    public HistoryWindow() : this(Settings.Current.SaveDir) => _followSettings = true;

    public HistoryWindow(string saveDir)
    {
        _saveDir = saveDir;
        InitializeComponent();
        DarkWindow.Attach(this, Root);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); _ = RefreshAsync(); };
        Loaded += (_, _) => { WatchFolder(); _ = RefreshAsync(); };
        Activated += (_, _) => { if (IsLoaded) _ = RefreshAsync(); };
        Settings.Changed += Settings_Changed;
        Closed += (_, _) =>
        {
            _closed = true;
            _refreshTimer.Stop();
            _watcher?.Dispose();
            _previewRevision++;
            Settings.Changed -= Settings_Changed;
        };
    }

    private CaptureEntry? Selected => Captures.SelectedItem as CaptureEntry;

    private void Settings_Changed()
    {
        if (!_followSettings || _closed) return;
        _saveDir = Settings.Current.SaveDir;
        WatchFolder();
        _ = RefreshAsync();
    }

    private void WatchFolder()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            if (!Directory.Exists(_saveDir)) return;
            _watcher = new FileSystemWatcher(_saveDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Created += File_Changed;
            _watcher.Changed += File_Changed;
            _watcher.Deleted += File_Changed;
            _watcher.Renamed += File_Changed;
            _watcher.Error += (_, _) => ScheduleRefresh();
            _watcher.EnableRaisingEvents = true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
    }

    private void File_Changed(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (_closed) return;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_closed) return;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }));
    }

    private async Task RefreshAsync()
    {
        if (_closed) return;
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        string directory = _saveDir;
        try
        {
            var result = await Task.Run(() => CaptureHistory.Scan(directory));
            if (_closed || directory != _saveDir) return;
            _entries = result.Entries;
            ApplyFilter();
            SetStatus(result.Error);
        }
        catch (Exception ex)
        {
            if (!_closed) SetStatus($"Could not load captures: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
            if (_refreshAgain) { _refreshAgain = false; _ = RefreshAsync(); }
        }
    }

    private void ApplyFilter()
    {
        if (Captures is null || SearchBox is null) return;
        string? selected = Selected?.Path;
        string search = SearchBox.Text.Trim();
        var shown = _entries.Where(e => (_filter == "all" || e.IsVideo == (_filter == "recordings")) &&
            (search.Length == 0 || $"{e.Name} {e.Modified:yyyy-MM-dd} {e.Details}".Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToList();
        // Replacing ItemsSource can briefly remove selection before restoring it.
        // Decode only the final selection, including an equal item refreshed from disk.
        _applyingFilter = true;
        try
        {
            Captures.ItemsSource = shown;
            Captures.SelectedItem = shown.FirstOrDefault(e => e.Path.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? shown.FirstOrDefault();
        }
        finally { _applyingFilter = false; }
        EmptyState.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool filtered = search.Length > 0 || _filter != "all";
        EmptyTitle.Text = filtered ? "No matching captures" : "No captures yet";
        EmptyHint.Text = filtered ? "Try another search or choose All." : "New screenshots and recordings appear here.";
        CountLabel.Text = filtered ? $"{shown.Count} of {_entries.Count} captures" : $"{shown.Count} capture{(shown.Count == 1 ? "" : "s")}";
        _ = UpdatePreviewAsync();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _filter = (string)((RadioButton)sender).Tag;
        ApplyFilter();
    }

    private void Capture_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!_applyingFilter) _ = UpdatePreviewAsync();
    }

    private async Task UpdatePreviewAsync()
    {
        int revision = ++_previewRevision;
        var entry = Selected;
        Preview.Source = null;
        PreviewMessage.Visibility = Visibility.Visible;
        PreviewMessage.ToolTip = null;
        PreviewMessage.Text = entry is null ? "Select a capture to preview it" : "Loading preview…";
        SelectedName.Text = entry?.Name ?? "";
        SelectedDetails.Text = entry?.Details ?? "";
        OpenButton.Content = entry?.IsVideo == true ? "Open trimmer" : "Open editor";
        CopyButton.Content = entry?.IsVideo == true ? "Copy file" : "Copy image";
        UpdateActions();
        if (entry is null) return;
        try
        {
            await _previewGate.WaitAsync();
            (BitmapSource? image, string? info) preview;
            try
            {
                if (_closed || revision != _previewRevision) return;
                preview = await Task.Run(() =>
                {
                    if (!entry.IsVideo) return ((BitmapSource?)CaptureHistory.LoadImage(entry.Path, 1400), (string?)null);
                    var (frames, duration) = Recording.VideoThumbnails.Extract(entry.Path, 1, 480);
                    return (frames.FirstOrDefault(), (string?)$"{duration.TotalMinutes:0}:{duration.Seconds:00}  ·  {entry.Details}");
                });
            }
            finally { _previewGate.Release(); }
            if (_closed || revision != _previewRevision) return;
            Preview.Source = preview.image;
            PreviewMessage.Text = "Preview unavailable. You can still open or copy this file.";
            PreviewMessage.Visibility = preview.image is null ? Visibility.Visible : Visibility.Collapsed;
            if (preview.info is not null) SelectedDetails.Text = preview.info;
        }
        catch (Exception ex)
        {
            if (_closed || revision != _previewRevision) return;
            PreviewMessage.Text = "Preview unavailable. The file may be in use, incomplete or unreadable.";
            PreviewMessage.ToolTip = ex.Message;
        }
    }

    private void UpdateActions()
    {
        bool enabled = Selected is not null && !_acting;
        OpenButton.IsEnabled = CopyButton.IsEnabled = FolderButton.IsEnabled = enabled;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected;
        if (entry is null || _acting) return;
        if (_editors.TryGetValue(entry.Path, out var existing) && existing.IsLoaded)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        _acting = true;
        UpdateActions();
        try
        {
            if (!File.Exists(entry.Path)) throw new FileNotFoundException("This capture is no longer in the save folder.");
            BitmapSource? image = entry.IsVideo ? null : await Task.Run(() => CaptureHistory.LoadImage(entry.Path));
            if (_closed) return;
            Window editor = entry.IsVideo ? new TrimWindow(entry.Path) : new EditorWindow(entry.Path, image!);
            _editors[entry.Path] = editor;
            editor.Closed += (_, _) => { _editors.Remove(entry.Path); if (!_closed) _ = RefreshAsync(); };
            if (editor is EditorWindow screenshot) screenshot.ImageSaved += _ => { if (!_closed) ScheduleRefresh(); };
            var trace = new PerformanceTrace(entry.IsVideo ? "history-trim-open" : "history-editor-open");
            trace.TrackWindow(editor);
            editor.Show();
            editor.Activate();
            SetStatus(null);
        }
        catch (Exception ex) { if (!_closed) SetStatus($"Could not open capture: {ex.Message}"); }
        finally { _acting = false; if (!_closed) UpdateActions(); }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected;
        if (entry is null || _acting) return;
        _acting = true;
        UpdateActions();
        try
        {
            if (!File.Exists(entry.Path)) throw new FileNotFoundException("This capture is no longer in the save folder.");
            BitmapSource? image = entry.IsVideo ? null : await Task.Run(() => CaptureHistory.LoadImage(entry.Path));
            if (_closed) return;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (image is not null) Clipboard.SetImage(image);
                    else Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { entry.Path });
                    break;
                }
                catch when (attempt < 3) { await Task.Delay(60); if (_closed) return; }
            }
            SetStatus(entry.IsVideo ? "Recording copied. Paste it as a file." : "Image copied.");
        }
        catch (Exception ex) { if (!_closed) SetStatus($"Could not copy capture: {ex.Message}"); }
        finally { _acting = false; if (!_closed) UpdateActions(); }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        try
        {
            if (!File.Exists(entry.Path)) throw new FileNotFoundException("This capture is no longer in the save folder.");
            Process.Start("explorer.exe", $"/select,\"{entry.Path}\"");
        }
        catch (Exception ex) { SetStatus($"Could not show capture: {ex.Message}"); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) { WatchFolder(); _ = RefreshAsync(); }
    private void Capture_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Captures, e.OriginalSource as DependencyObject) is ListBoxItem) Open_Click(sender, e);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.F5) { Refresh_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (!typing && e.Key == Key.Enter) { Open_Click(sender, e); e.Handled = true; }
        else if (!typing && e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { Copy_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void SetStatus(string? message)
    {
        Status.Text = message ?? "";
        Status.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
