using System.IO;
using System.Runtime.InteropServices;

namespace WinSnipper;

/// <summary>
/// Opt-in housekeeping: captures older than Settings.AutoDeleteDays are moved
/// to the Recycle Bin (never hard-deleted). Only files WinSnipper itself
/// created — "Snip *.png" / "Recording *.mp4" in the save folder — are
/// touched, so anything else the user keeps there is safe.
/// </summary>
public static class AutoCleanup
{
    public static async Task RunLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(60)); // stay out of startup's way
        while (true)
        {
            try { CleanOnce(); }
            catch (Exception ex) { Util.LogCrash("AutoCleanup", ex); }
            await Task.Delay(TimeSpan.FromHours(12));
        }
    }

    public static void CleanOnce()
    {
        int days = Settings.Current.AutoDeleteDays;
        if (days <= 0) return;
        string dir = Settings.Current.SaveDir;
        if (!Directory.Exists(dir)) return;

        var cutoff = DateTime.Now.AddDays(-days);
        var victims = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(file);
            bool ours =
                (name.StartsWith("Snip ", StringComparison.OrdinalIgnoreCase) &&
                 name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ||
                (name.StartsWith("Recording ", StringComparison.OrdinalIgnoreCase) &&
                 name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            if (!ours) continue;

            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    victims.Add(file);
            }
            catch { }
        }

        if (victims.Count > 0)
            RecycleFiles(victims);
    }

    /// <summary>Batch send-to-Recycle-Bin, silent, no confirmation UI.</summary>
    private static void RecycleFiles(List<string> paths)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3, // FO_DELETE
            pFrom = string.Join("\0", paths) + "\0\0",
            fFlags = 0x40 | 0x10 | 0x4 | 0x400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        _ = SHFileOperation(ref op);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);
}
