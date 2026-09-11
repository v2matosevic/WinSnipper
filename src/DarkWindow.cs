using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinSnipper;

/// <summary>
/// Shared plumbing for the frameless editing windows: dark DWM frame with
/// Windows 11 rounded corners, the three caption-button commands, and a
/// maximized state that stays inside the work area.
/// </summary>
internal static class DarkWindow
{
    /// <param name="root">Top-level element that gets inset while maximized.</param>
    public static void Attach(Window w, FrameworkElement root)
    {
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            int dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            int round = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
            _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        };

        void Fit() => root.Margin = w.WindowState == WindowState.Maximized
            ? Monitors.MaximizedInset(w)
            : new Thickness(0);
        // The rect is only final once the state change has been laid out.
        w.StateChanged += (_, _) => w.Dispatcher.BeginInvoke(Fit, DispatcherPriority.Loaded);
        w.SizeChanged += (_, _) => { if (w.WindowState == WindowState.Maximized) Fit(); };

        w.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
            (_, _) => SystemCommands.MinimizeWindow(w)));
        w.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) =>
        {
            if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w);
            else SystemCommands.MaximizeWindow(w);
        }));
        w.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => w.Close()));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
