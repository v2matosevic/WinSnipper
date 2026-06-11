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

        // Headless smoke test: capture the screen, write a PNG, exit.
        if (e.Args.Contains("--selftest"))
        {
            var (shot, _) = ScreenCapture.CaptureVirtualScreen();
            Util.SavePng(shot, Path.Combine(Util.SnipsDir, "_selftest.png"));
            Shutdown(0);
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

    // Small delay so the tray context menu has closed before we freeze the screen.
    private async void SnipFromMenu()
    {
        await Task.Delay(300);
        _snips.StartSnip();
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
