using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinSnipper;

internal static class WindowsCaptureIntegration
{
    internal const string ProgId = "WinSnipper.ScreenClip";
    private const string Capabilities = @"Software\WinSnipper\Capabilities";
    private const string UserChoice = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\ms-screenclip\UserChoice";
    internal static bool IsDefault
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserChoice);
            return StringComparer.OrdinalIgnoreCase.Equals(key?.GetValue("ProgId") as string, ProgId);
        }
    }

    internal static void Register(string exe)
    {
        Register(Registry.CurrentUser, exe);
        SHChangeNotify(0x08000000 /* ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    // Registry root is injectable for private test fixtures. Never write UserChoice:
    // the protected default selection belongs to Windows and the owner.
    internal static void Register(RegistryKey root, string exe)
    {
        exe = System.IO.Path.GetFullPath(exe);
        if (exe.Contains('"')) throw new ArgumentException("Invalid executable path.");
        using (var caps = root.CreateSubKey(Capabilities))
        {
            caps.SetValue("ApplicationName", "WinSnipper");
            caps.SetValue("ApplicationDescription", "Capture screenshots and screen recordings with WinSnipper.");
            caps.SetValue("ApplicationIcon", $"\"{exe}\",0");
            using var associations = caps.CreateSubKey("URLAssociations");
            associations.SetValue("ms-screenclip", ProgId);
        }
        using (var registered = root.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue("WinSnipper", Capabilities);
        using (var prog = root.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            prog.SetValue("", "WinSnipper screen capture");
            prog.SetValue("URL Protocol", "");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using var command = prog.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" --capture-uri \"%1\"");
        }
    }

    internal static void OpenDefaults()
    {
        Register(Environment.ProcessPath!);
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=WinSnipper") { UseShellExecute = true });
    }

    internal static void NotifyKeyboardChange() => _ = Task.Run(() =>
    {
        // Standard configuration notification, not keyboard input. Keep the
        // bounded broadcast off WPF so a busy app cannot stall capture setup.
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* SETTINGCHANGE */, IntPtr.Zero,
            @"Control Panel\Keyboard", 0x0002 /* ABORTIFHUNG */, 100, out _);
    });

    internal static bool TryParse(string value, out CaptureIntent intent)
    {
        intent = CaptureIntent.Screenshot;
        if (value.Length > 4096 || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("ms-screenclip", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        // This is a default UI handler, not the packaged Snipping Tool callback
        // API. Never follow a caller-provided redirect or open supplied paths.
        string query = Uri.UnescapeDataString(uri.Query);
        if (query.Contains("redirect-uri", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Host.Length == 0 && uri.AbsolutePath is "" or "/") return true;
        if (!uri.Host.Equals("capture", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.AbsolutePath.Equals("/video", StringComparison.OrdinalIgnoreCase)) { intent = CaptureIntent.Recording; return true; }
        return uri.AbsolutePath.Equals("/image", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
