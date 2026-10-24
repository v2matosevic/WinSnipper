using Microsoft.Win32;

namespace WinSnipper;

/// <summary>
/// Keeps bare PrintScreen consistent with the selected Windows default.
/// Persist its original value before any write, including legacy migration.
/// </summary>
public static class SnippingTool
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingTool";
    public static string? LastError { get; private set; }

    /// <summary>Applies the current setting. Safe to call on every startup.</summary>
    public static void ApplyPolicy()
    {
        LastError = null;
        var s = Settings.Current;
        try
        {
            if (Apply(s, Registry.CurrentUser, WindowsCaptureIntegration.IsDefault, s.Save))
                WindowsCaptureIntegration.NotifyKeyboardChange();
        }
        catch (Exception ex)
        {
            LastError = "PrintScreen settings could not be updated. Save settings to retry.";
            Util.LogCrash("PrintScreen policy", ex);
        }
    }

    internal static bool Apply(Settings s, RegistryKey root, bool isDefault, Action persist)
    {
        using var key = root.CreateSubKey(KeyPath, writable: true);
        var raw = key.GetValue(ValueName);
        if (raw is not null && (raw is not int value || value is < 0 or > 1 || key.GetValueKind(ValueName) != RegistryValueKind.DWord))
            throw new InvalidOperationException("PrintScreen binding is not a DWORD; left unchanged.");
        int current = raw as int? ?? -1;
        if (s.ReplaceSnippingTool)
        {
            int target = isDefault ? 1 : 0;
            if (s.PrintScreenBindingCaptured && current == target && s.AppliedPrintScreenBinding == target) return false;
            if (!s.PrintScreenBindingCaptured)
            {
                bool legacyOwned = s.LegacyPrintScreenBinding && current == 0;
                if (current == target && !legacyOwned) return false;
                if (!legacyOwned) s.SavedPrintScreenBinding = current;
                s.PrintScreenBindingCaptured = true;
            }
            s.AppliedPrintScreenBinding = target;
            persist(); // snapshot reaches disk before changing the registry
            if (current != target) key.SetValue(ValueName, target, RegistryValueKind.DWord);
            return current != target;
        }
        else
        {
            bool legacyOwned = s.LegacyPrintScreenBinding && current == 0;
            if (!s.PrintScreenBindingCaptured && !legacyOwned) return false;
            int applied = s.AppliedPrintScreenBinding ?? 0;
            if (current == applied)
            {
                if (s.SavedPrintScreenBinding < 0) key.DeleteValue(ValueName, throwOnMissingValue: false);
                else key.SetValue(ValueName, s.SavedPrintScreenBinding, RegistryValueKind.DWord);
            }
            // Preserve changes made outside WinSnipper instead of overwriting them.
            s.PrintScreenBindingCaptured = false;
            s.AppliedPrintScreenBinding = null;
            s.SavedPrintScreenBinding = -1;
            s.LegacyPrintScreenBinding = false;
            persist();
            return current == applied;
        }
    }
}
