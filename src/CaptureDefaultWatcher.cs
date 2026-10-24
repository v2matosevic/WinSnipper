using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinSnipper;

/// <summary>Read-only registry notification. Windows owns the protected default
/// choice; we only adapt our PrintScreen policy after that choice changes.</summary>
internal sealed class CaptureDefaultWatcher : IDisposable
{
    private readonly ManualResetEvent _cancel = new(false);
    private readonly Thread _thread;
    private volatile bool _ready;
    private int _disposed;
    internal bool IsReady => _ready;

    internal CaptureDefaultWatcher(RegistryKey key, Action changed)
    {
        _thread = new Thread(() =>
        {
            using (key)
            using (var notification = new AutoResetEvent(false))
            {
                var waits = new WaitHandle[] { _cancel, notification };
                while (RegNotifyChangeKeyValue(key.Handle, true, 5 /* NAME | LAST_SET */,
                    notification.SafeWaitHandle.DangerousGetHandle(), true) == 0)
                {
                    _ready = true;
                    if (WaitHandle.WaitAny(waits) == 0) break;
                    try { changed(); } catch (Exception ex) { Util.LogCrash("Capture default notification", ex); }
                }
            }
        }) { IsBackground = true, Name = "WinSnipper.CaptureDefault" };
        _thread.Start();
    }

    internal static CaptureDefaultWatcher? Start(Action changed)
    {
        var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations");
        return key is null ? null : new CaptureDefaultWatcher(key, changed);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Set();
        if (_thread.Join(TimeSpan.FromSeconds(1))) _cancel.Dispose();
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool subtree, uint filter, IntPtr notification, bool asynchronous);
}
