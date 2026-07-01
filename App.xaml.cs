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
    private readonly Recording.RecordingManager _recordings = new();

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

        // Open the trim editor for an existing recording, no tray/hotkeys.
        if (e.Args.Length == 2 && e.Args[0] == "--trim" && File.Exists(e.Args[1]))
        {
            new TrimWindow(e.Args[1]).Show();
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
            onNewRecording: () => RecordFromMenu(),
            onSettings: ShowSettings,
            onExit: Shutdown);
        _recordings.OnError = msg => _tray?.ShowError(msg);

        try
        {
            _hook = new KeyboardHook();
            _hook.HotkeyPressed += () => Dispatcher.BeginInvoke(_snips.StartSnip);
            _hook.RecordHotkeyPressed += () => Dispatcher.BeginInvoke(_recordings.Toggle);
            StartHookWatchdog();
        }
        catch (Exception ex)
        {
            _tray.ShowError($"Could not install the {Util.CurrentHotkeyDisplay} hook: {ex.Message}\nUse the tray menu to snip.");
        }

        _ = CheckForUpdatesLoop();
        _ = AutoCleanup.RunLoopAsync();
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
        int exit = 0;
        try
        {
            var (shot, _) = ScreenCapture.CaptureVirtualScreen();
            Util.SavePng(shot, Path.Combine(Util.SnipsDir, "_selftest.png"));
            string? ocr = Util.OcrSupported ? await Util.OcrAsync(shot) : "(OCR not in this build)";
            File.WriteAllText(Path.Combine(Util.SnipsDir, "_selftest.txt"), ocr ?? "(OCR unavailable)");

            // Recording round-trip: 2s of a screen corner → MP4 → trim to the middle 1s.
            string vid = Path.Combine(Util.SnipsDir, "_selftest.mp4");
            string trimmed = Path.Combine(Util.SnipsDir, "_selftest_trim.mp4");
            var rec = new Recording.ScreenRecorder(vid, new System.Windows.Int32Rect(0, 0, 320, 240), 30, includeCursor: true);
            rec.Start();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (rec.Elapsed < TimeSpan.FromSeconds(2) && rec.Error is null && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            var frame = await rec.StopAsync();
            if (rec.Error is not null) throw rec.Error;
            if (frame is null || new FileInfo(vid).Length < 1000)
                throw new Exception("selftest recording produced no usable MP4");
            await Task.Run(() => Recording.VideoTrimmer.Trim(
                vid, trimmed, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500)));
            if (new FileInfo(trimmed).Length < 500)
                throw new Exception("selftest trim produced no usable MP4");
        }
        catch (Exception ex)
        {
            Util.LogCrash("SelfTest", ex);
            try { File.WriteAllText(Path.Combine(Util.SnipsDir, "_selftest_error.txt"), ex.ToString()); } catch { }
            exit = 1;
        }
        finally
        {
            Shutdown(exit);
        }
    }

    // Small delay so the tray context menu has closed before we freeze the screen.
    private async void SnipFromMenu()
    {
        await Task.Delay(300);
        _snips.StartSnip();
    }

    private async void RecordFromMenu()
    {
        await Task.Delay(300);
        _recordings.Toggle();
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
        // Don't leave a truncated MP4 behind if we quit mid-recording.
        if (_recordings.IsRecording)
            try { _recordings.StopAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
        _hook?.Dispose();
        _tray?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
