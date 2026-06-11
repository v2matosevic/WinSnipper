using System.IO;
using System.Windows;

namespace WinSnipper;

public partial class SettingsWindow : Window
{
    private readonly Settings _draft = Settings.Current.Clone();
    private bool _capturing;

    public SettingsWindow()
    {
        InitializeComponent();

        HotkeyBox.Content = Util.CurrentHotkeyDisplay;
        DismissSlider.Value = _draft.DismissSeconds;
        DismissSlider.ValueChanged += (_, e) => DismissLabel.Text = $"{(int)e.NewValue} s";
        DismissLabel.Text = $"{_draft.DismissSeconds} s";
        SaveDirText.Text = _draft.SaveDir;
        ClipboardCheck.IsChecked = _draft.CopyToClipboard;
        StartupCheck.IsChecked = StartupManager.IsEnabled();

        Closed += (_, _) => StopCapture();
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

    // ---------- hotkey capture ----------

    private void HotkeyBox_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            StopCapture();
            return;
        }
        _capturing = true;
        HotkeyBox.Content = "Press a combination…";
        KeyboardHook.CaptureInterceptor = OnCaptureKey;
    }

    private void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        KeyboardHook.CaptureInterceptor = null;
        HotkeyBox.Content = Util.FormatHotkey(_draft.ModWin, _draft.ModCtrl, _draft.ModAlt, _draft.ModShift, _draft.HotkeyVk);
    }

    /// <summary>Runs from the low-level hook (UI thread). Swallows every key while capturing.</summary>
    private bool OnCaptureKey(uint vk)
    {
        if (vk == 0x1B) // Esc cancels
        {
            Dispatcher.BeginInvoke(StopCapture);
            return true;
        }

        // Bare modifiers: swallow, keep waiting for the actual key.
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5))
            return true;

        bool win = KeyboardHook.IsKeyDown(0x5B) || KeyboardHook.IsKeyDown(0x5C);
        bool shift = KeyboardHook.IsKeyDown(0x10);
        bool ctrl = KeyboardHook.IsKeyDown(0x11);
        bool alt = KeyboardHook.IsKeyDown(0x12);

        // A global capture hotkey needs at least one modifier.
        if (!(win || shift || ctrl || alt))
            return true;

        _draft.HotkeyVk = vk;
        _draft.ModWin = win;
        _draft.ModShift = shift;
        _draft.ModCtrl = ctrl;
        _draft.ModAlt = alt;
        Dispatcher.BeginInvoke(StopCapture);
        return true;
    }

    // ---------- fields ----------

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where snips are saved",
            InitialDirectory = Directory.Exists(_draft.SaveDir) ? _draft.SaveDir : Settings.DefaultSaveDir,
        };
        if (dlg.ShowDialog(this) == true)
        {
            _draft.SaveDir = dlg.FolderName;
            SaveDirText.Text = dlg.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StopCapture();
        _draft.DismissSeconds = (int)DismissSlider.Value;
        _draft.CopyToClipboard = ClipboardCheck.IsChecked == true;
        _draft.Save();
        try { StartupManager.SetEnabled(StartupCheck.IsChecked == true); } catch { }
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
