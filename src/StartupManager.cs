using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace WinSnipper;

/// <summary>Startup toggle, using the supervised task for opt-in elevated installs.</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinSnipper";
    private const string TaskName = "WinSnipper Keep-Alive";

    public static bool IsEnabled()
    {
        if (ElevatedTaskStartup(null) is bool enabled) return enabled;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        if (ElevatedTaskStartup(enabled).HasValue)
        {
            // Never recreate a medium-integrity Run launch that can claim
            // the single-instance mutex before the elevated logon task.
            using var oldKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            oldKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static bool? ElevatedTaskStartup(bool? enabled)
    {
        object? service = null, folder = null, task = null, definition = null, principal = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return null;
            service = Activator.CreateInstance(type)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            task = ((dynamic)folder).GetTask(TaskName);
            definition = ((dynamic)task).Definition;
            principal = ((dynamic)definition).Principal;
            if ((int)((dynamic)principal).RunLevel != 1) return null; // TASK_RUNLEVEL_HIGHEST
            if (enabled.HasValue) ((dynamic)task).Enabled = enabled.Value;
            return (bool)((dynamic)task).Enabled;
        }
        catch (COMException) when (!enabled.HasValue) { return null; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; } // no task
        finally
        {
            foreach (var item in new[] { principal, definition, task, folder, service })
                if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }
}
