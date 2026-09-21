using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinSnipper;

/// <summary>
/// Low-level keyboard hook that intercepts the capture hotkeys before the OS
/// hotkey (Windows Snipping Tool) can handle them. RegisterHotKey cannot claim
/// Win+Shift+S because the shell already owns it; a WH_KEYBOARD_LL hook sees
/// the keystroke first and can swallow it.
///
/// The hook runs on its own thread with its own message pump, and that is
/// load-bearing. Windows delivers LL hook callbacks on the thread that
/// installed the hook, so a hook installed from the WPF UI thread queues
/// behind every render, window construction and GC pause. Once a callback
/// exceeds LowLevelHooksTimeout Windows passes the keystroke onward and can
/// silently remove the hook. Keep application work on workers, not callbacks.
/// System-wide scheduling/GC stalls can still delay a managed hook, so the
/// message pump also periodically replaces it without depending on the UI.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_TIMER = 0x0113;
    private const int WM_USER = 0x0400;
    private const int WM_APP_REINSTALL = 0x8001; // WM_APP + 1, posted to the hook thread
    private const uint PM_NOREMOVE = 0x0000;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const uint VK_SNAPSHOT = 0x2C; // PrintScreen

    private const uint KEYEVENTF_KEYUP = 0x0002;

    // KBDLLHOOKSTRUCT field offsets. Reading the two fields we need beats
    // marshalling the whole struct on every keystroke in the system.
    private const int OffsetVkCode = 0;
    private const int OffsetTime = 12;

    private readonly LowLevelKeyboardProc _proc; // kept as a field so GC never collects the delegate
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private IntPtr _hookId;
    private Exception? _startError;
    private volatile bool _disposed;
    private readonly bool[] _swallowed = new bool[256];

    public event Action<uint>? HotkeyPressed;
    public event Action<uint>? RecordHotkeyPressed;

    /// <summary>
    /// While set, every key-down is routed here first (used by the settings
    /// window to record a new hotkey, including Win-combos the OS would
    /// otherwise handle). Return true to swallow the keystroke. Called on the
    /// hook thread, not the UI thread.
    /// </summary>
    public static Func<uint, bool>? CaptureInterceptor;

    public static bool IsKeyDown(int vk) => IsDown(vk);

    public KeyboardHook()
    {
        _ = Settings.Current; // Load settings before installing a system-wide callback.
        _proc = Callback;
        _thread = new Thread(ThreadMain)
        {
            Name = "WinSnipper hotkey hook",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
        if (_startError is not null)
            throw _startError;
        if (_hookId == IntPtr.Zero)
            throw new Win32Exception("The keyboard hook thread did not start.");
    }

    // ---------- hook thread ----------

    private void ThreadMain()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            // Force the message queue into existence before anyone can post to it.
            PeekMessage(out _, IntPtr.Zero, WM_USER, WM_USER, PM_NOREMOVE);

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hookId == IntPtr.Zero)
                _startError = new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            _startError = ex;
        }
        finally
        {
            _ready.Set();
        }

        if (_hookId == IntPtr.Zero) return;

        // Recovery must run even when the UI dispatcher is stuck. No polling
        // thread or simulated keystrokes: one native timer on this message pump.
        var timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 15000, IntPtr.Zero);
        // WM_QUIT (posted by Dispose) returns 0 and ends the loop; -1 is an error.
        while (true)
        {
            int got = GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (got <= 0) break;
            if (msg.message == WM_APP_REINSTALL || msg.message == WM_TIMER)
                ReinstallHere();
        }

        if (timer != UIntPtr.Zero) KillTimer(IntPtr.Zero, timer);

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private void ReinstallHere()
    {
        if (_disposed) return;
        // Keep the old hook if replacement fails, and leave no unhooked gap.
        var replacement = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (replacement == IntPtr.Zero) return;
        var previous = _hookId;
        _hookId = replacement;
        if (previous != IntPtr.Zero) UnhookWindowsHookEx(previous);
        for (int vk = 0; vk < _swallowed.Length; vk++)
            if (_swallowed[vk] && !IsDown(vk)) _swallowed[vk] = false;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        uint vk = (uint)Marshal.ReadInt32(lParam, OffsetVkCode);
        if (vk < _swallowed.Length && _swallowed[vk])
        {
            if (msg == WM_KEYUP || msg == WM_SYSKEYUP) _swallowed[vk] = false;
            // Suppress repeats and the matching release, even if modifiers
            // were released first. One physical press means one capture.
            return (IntPtr)1;
        }
        if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        if (CaptureInterceptor is { } capture && capture(vk))
            return (IntPtr)1;

        // Fast reject. This callback sits in front of every keystroke on the
        // machine, so anything that is not one of our keys has to leave here
        // before we touch the modifier state.
        var s = Settings.Current;
        bool printScreen = vk == VK_SNAPSHOT && s.ReplaceSnippingTool;
        if (!printScreen && vk != s.HotkeyVk && vk != s.RecHotkeyVk)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        uint time = (uint)Marshal.ReadInt32(lParam, OffsetTime);

        if (printScreen && !IsDown(VK_LWIN) && !IsDown(VK_RWIN)
            && !IsDown(VK_SHIFT) && !IsDown(VK_CONTROL) && !IsDown(VK_MENU))
        {
            Dispatch(vk, time, recording: false);
            return (IntPtr)1;
        }

        bool win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
        bool shift = IsDown(VK_SHIFT);
        bool ctrl = IsDown(VK_CONTROL);
        bool alt = IsDown(VK_MENU);

        if (Matches(vk, win, shift, ctrl, alt,
                s.HotkeyVk, s.ModWin, s.ModShift, s.ModCtrl, s.ModAlt))
        {
            SuppressStartMenu(s.ModWin);
            Dispatch(vk, time, recording: false);
            return (IntPtr)1; // swallow
        }
        if (Matches(vk, win, shift, ctrl, alt,
                s.RecHotkeyVk, s.RecModWin, s.RecModShift, s.RecModCtrl, s.RecModAlt))
        {
            SuppressStartMenu(s.RecModWin);
            Dispatch(vk, time, recording: true);
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void Dispatch(uint vk, uint timestamp, bool recording)
    {
        if (vk < _swallowed.Length) _swallowed[vk] = true;
        // Never run application subscribers inside the Windows hook timeout.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed) return;
            try
            {
                if (recording) RecordHotkeyPressed?.Invoke(timestamp);
                else HotkeyPressed?.Invoke(timestamp);
            }
            catch (Exception ex) { Util.LogCrash("Hotkey dispatch", ex); }
        });
    }

    private static bool Matches(uint vk, bool win, bool shift, bool ctrl, bool alt,
        uint cfgVk, bool cfgWin, bool cfgShift, bool cfgCtrl, bool cfgAlt) =>
        vk == cfgVk
        && win == cfgWin && shift == cfgShift && ctrl == cfgCtrl && alt == cfgAlt
        && (cfgWin || cfgShift || cfgCtrl || cfgAlt);

    /// <summary>
    /// The OS saw Win-down but will never see the key we swallow; without a
    /// dummy keystroke it would open the Start menu on Win-up.
    /// </summary>
    private static void SuppressStartMenu(bool usesWin)
    {
        if (!usesWin) return;
        keybd_event(0xFF, 0, 0, UIntPtr.Zero);
        keybd_event(0xFF, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Windows removes LL hooks whose callback ever exceeds the hook timeout.
    /// Off the UI thread that should no longer happen, but re-arming after
    /// sleep or a lock costs nothing and covers the case where it did.
    /// </summary>
    public void Reinstall()
    {
        if (_disposed || _threadId == 0) return;
        PostThreadMessage(_threadId, WM_APP_REINSTALL, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint interval, IntPtr callback);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
