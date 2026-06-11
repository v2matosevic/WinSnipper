using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

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

        InstallCrashHandlers();

        _tray = new TrayIcon(
            onNewSnip: () => SnipFromMenu(),
            onSettings: ShowSettings,
            onExit: Shutdown);

        try
        {
            _hook = new KeyboardHook();
            _hook.HotkeyPressed += () => Dispatcher.BeginInvoke(_snips.StartSnip);
            StartHookWatchdog();
        }
        catch (Exception ex)
        {
            _tray.ShowError($"Could not install the {Util.CurrentHotkeyDisplay} hook: {ex.Message}\nUse the tray menu to snip.");
        }

        _ = CheckForUpdatesLoop();
    }

    // ---------- stability ----------

    private void InstallCrashHandlers()
    {
        // UI-thread exceptions: log, tell the user, keep the app alive.
        DispatcherUnhandledException += (_, e) =>
        {
            Util.LogCrash("Dispatcher", e.Exception);
            _tray?.ShowError($"Something went wrong: {e.Exception.Message}\nDetails: %APPDATA%\\WinSnipper\\crash.log");
            e.Handled = true;
        };
        // Background/finalizer exceptions: at least leave a trace before dying.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Util.LogCrash("AppDomain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Util.LogCrash("Task", e.Exception);
            e.SetObserved();
        };
    }

    // Windows silently drops LL keyboard hooks after a slow callback (sleep,
    // heavy load) — the classic "hotkey stopped working". Re-arm periodically
    // and on resume/unlock.
    private void StartHookWatchdog()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        timer.Tick += (_, _) => _hook?.Reinstall();
        timer.Start();

        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                Dispatcher.BeginInvoke(() => _hook?.Reinstall());
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
                Dispatcher.BeginInvoke(() => _hook?.Reinstall());
        };
    }

    // Once a day, see if GitHub has a newer release; a tray balloon links to it.
    private async Task CheckForUpdatesLoop()
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("WinSnipper");
                string json = await http.GetStringAsync(
                    "https://api.github.com/repos/v2matosevic/WinSnipper/releases/latest");
                using var doc = JsonDocument.Parse(json);
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                string url = doc.RootElement.GetProperty("html_url").GetString() ?? "";
                if (Version.TryParse(tag.TrimStart('v'), out var latest))
                {
                    var current = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
                    if (latest > new Version(current.Major, current.Minor, current.Build))
                        _tray?.ShowUpdateAvailable(tag, url);
                }
            }
            catch
            {
                // offline / rate-limited — try again next cycle
            }
            await Task.Delay(TimeSpan.FromHours(24));
        }
    }

    private async void RunSelfTest()
    {
        try
        {
            var (shot, _) = ScreenCapture.CaptureVirtualScreen();
            Util.SavePng(shot, Path.Combine(Util.SnipsDir, "_selftest.png"));
            string? ocr = Util.OcrSupported ? await Util.OcrAsync(shot) : "(OCR not in this build)";
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
