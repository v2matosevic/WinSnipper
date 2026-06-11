using System.IO;
using System.Windows;

namespace WinSnipper;

public partial class App : Application
{
    private static Mutex? _mutex;
    private KeyboardHook? _hook;
    private TrayIcon? _tray;
    private readonly SnipManager _snips = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless smoke test: capture the screen, write a PNG, OCR it, exit.
        // Must stay async — blocking on WinRT from the STA thread deadlocks.
        if (e.Args.Contains("--selftest"))
        {
            RunSelfTest();
            return;
        }

        _mutex = new Mutex(true, "WinSnipper_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown(0);
            return;
        }

        _tray = new TrayIcon(
            onNewSnip: () => SnipFromMenu(),
            onSettings: ShowSettings,
            onExit: Shutdown);

        try
        {
            _hook = new KeyboardHook();
            _hook.HotkeyPressed += () => Dispatcher.BeginInvoke(_snips.StartSnip);
        }
        catch (Exception ex)
        {
            _tray.ShowError($"Could not install the Win+Shift+S hook: {ex.Message}\nUse the tray menu to snip.");
        }
    }

    private async void RunSelfTest()
    {
        try
        {
            var (shot, _) = ScreenCapture.CaptureVirtualScreen();
            Util.SavePng(shot, Path.Combine(Util.SnipsDir, "_selftest.png"));
            string? ocr = await Util.OcrAsync(shot);
            File.WriteAllText(Path.Combine(Util.SnipsDir, "_selftest.txt"), ocr ?? "(OCR unavailable)");
        }
        finally
        {
            Shutdown(0);
        }
    }

    // Small delay so the tray context menu has closed before we freeze the screen.
    private async void SnipFromMenu()
    {
        await Task.Delay(300);
        _snips.StartSnip();
    }

    private SettingsWindow? _settings;

    private void ShowSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow();
        _settings.Show();
        _settings.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _tray?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
