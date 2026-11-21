using System.Reflection;

namespace WinSnipper;

/// <summary>
/// Notices when the .NET runtime this process runs on has been removed from
/// disk. Windows services the shared runtime in place about once a month: the
/// new patch version is installed and the old folder is emptied while a
/// resident app is still running from it. Everything already loaded keeps
/// working; the first thing that needs an assembly not loaded yet fails
/// (tooltips for two weeks after 2026-10-30, the editor on 2026-11-15, then a
/// crash). The only cure is a new process, so the app restarts itself once
/// nothing is open. Warm-up also loads the assemblies its windows need, which
/// keeps the common paths alive until that restart.
/// </summary>
internal static class RuntimeHealth
{
    private static Timer? _timer;

    /// <summary>Framework files this process is running from. Empty when the runtime is bundled.</summary>
    internal static string[] FrameworkProbes() =>
        new[] { typeof(object).Assembly.Location, typeof(System.Windows.Window).Assembly.Location }
            .Where(path => !string.IsNullOrEmpty(path)).ToArray();

    /// <param name="probes">Files that must keep existing.</param>
    /// <param name="onRemoved">Raised on a pool thread each interval while any probe is missing.</param>
    internal static void Start(string[] probes, Action onRemoved, TimeSpan interval)
    {
        if (probes.Length == 0) return;
        _timer?.Dispose();
        _timer = new Timer(_ =>
        {
            try { if (!Intact(probes)) onRemoved(); }
            catch (Exception ex) { Util.LogCrash("RuntimeHealth", ex); }
        }, null, interval, interval);
    }

    internal static bool Intact(string[] probes)
    {
        foreach (string path in probes)
            if (!System.IO.File.Exists(path)) return false;
        return true;
    }

    /// <summary>
    /// Loads the framework assemblies WPF only asks for on first use: UI
    /// automation behind every tooltip and popup, and the full XAML namespace
    /// set the first x:Type lookup walks. Best effort; a missing optional
    /// assembly is not an error.
    /// </summary>
    internal static void Preload()
    {
        foreach (string name in new[]
        {
            "Accessibility", "UIAutomationTypes", "UIAutomationProvider", "UIAutomationClient",
            "System.Windows.Controls.Ribbon", "PresentationFramework.Aero2", "PresentationUI",
            "ReachFramework", "System.Windows.Input.Manipulations", "System.Drawing.Common",
        })
        {
            try { Assembly.Load(name); }
            catch { }
        }
    }
}
