using Microsoft.Win32;

namespace WinSnipper;

/// <summary>
/// Closes the other door into the built-in Snipping Tool.
///
/// Win+Shift+S never reaches the shell while WinSnipper's hook is alive, but
/// Windows 11 also opens the same tool from bare PrintScreen, and that one is
/// a user setting rather than a hotkey we can swallow — so we turn it off at
/// the source and take the key ourselves. The previous value is written into
/// settings.json first, so unticking the box puts Windows back exactly as it
/// was rather than guessing at a default.
/// </summary>
public static class SnippingTool
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingTool";

    /// <summary>Applies the current setting. Safe to call on every startup.</summary>
    public static void ApplyPolicy()
    {
        var s = Settings.Current;
        if (s.ReplaceSnippingTool)
            Disable(s);
        else
            Restore(s);
    }

    private static void Disable(Settings s)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null) return;

            int current = key.GetValue(ValueName) as int? ?? -1;
            if (current == 0) return; // already ours, and the saved value stands

            s.SavedPrintScreenBinding = current;
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
            s.Save();
        }
        catch
        {
            // A locked-down profile keeps its Snipping Tool; the hotkey still works.
        }
    }

    private static void Restore(Settings s)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) return;

            int current = key.GetValue(ValueName) as int? ?? -1;
            if (current != 0) return; // the user changed it themselves — leave it alone

            if (s.SavedPrintScreenBinding < 0)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            else
                key.SetValue(ValueName, s.SavedPrintScreenBinding, RegistryValueKind.DWord);

            s.SavedPrintScreenBinding = -1;
            s.Save();
        }
        catch
        {
            // Nothing we can do; the setting page is one click away.
        }
    }
}
