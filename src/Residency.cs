using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinSnipper;

/// <summary>
/// Keeps the warm path in RAM. On a machine short of memory Windows trims an
/// idle tray app first, and the next hotkey pays to read the runtime, WPF and
/// the capture path back from disk: seconds, against about 150 ms warm
/// (docs/PERFORMANCE-2026-10-29.md). A hard minimum working set stops the
/// trimming at what warm use needs; Windows still manages everything above it.
/// </summary>
internal static class Residency
{
    private const long MB = 1024 * 1024;
    private const long Floor = 128 * MB, Ceiling = 320 * MB, Headroom = 48 * MB;
    private const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1, QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;
    private const int ERROR_PRIVILEGE_NOT_HELD = 1314;

    /// <summary>Call once warm-up has touched the snip path. Returns a session-log line.</summary>
    internal static string Pin()
    {
        try
        {
            long workingSet;
            using (var self = Process.GetCurrentProcess()) workingSet = self.WorkingSet64;
            long min = Math.Clamp(workingSet + Headroom, Floor, Ceiling);
            long max = Math.Max(2 * min, 2 * workingSet);
            bool ok = Apply(min, max);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            // Users hold this privilege, disabled until a process asks for it.
            if (!ok && error == ERROR_PRIVILEGE_NOT_HELD && EnablePrivilege("SeIncreaseWorkingSetPrivilege"))
            {
                ok = Apply(min, max);
                error = ok ? 0 : Marshal.GetLastWin32Error();
            }
            return ok
                ? $"residency min={min / MB}MB working-set={workingSet / MB}MB"
                : $"residency unavailable error={error} working-set={workingSet / MB}MB";
        }
        catch (Exception ex) { return $"residency unavailable {ex.GetType().Name}"; }
    }

    private static bool Apply(long min, long max) =>
        SetProcessWorkingSetSizeEx(GetCurrentProcess(), (nuint)min, (nuint)max,
            QUOTA_LIMITS_HARDWS_MIN_ENABLE | QUOTA_LIMITS_HARDWS_MAX_DISABLE);

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0028 /* ADJUST_PRIVILEGES | QUERY */, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out long luid)) return false;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 /* SE_PRIVILEGE_ENABLED */ };
            // AdjustTokenPrivileges succeeds without assigning when the token lacks the privilege.
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges { public int Count; public long Luid; public int Attributes; }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nuint minimum, nuint maximum, uint flags);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state,
        int length, IntPtr previous, IntPtr returnLength);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
