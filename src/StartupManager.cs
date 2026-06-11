using Microsoft.Win32;

namespace WinSnipper;

/// <summary>HKCU Run-key toggle for starting WinSnipper at login.</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinSnipper";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
