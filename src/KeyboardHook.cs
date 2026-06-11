using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinSnipper;

/// <summary>
/// Low-level keyboard hook that intercepts Win+Shift+S before the OS hotkey
/// (Windows Snipping Tool) can handle it. RegisterHotKey cannot claim this
/// combo because the shell already owns it; a WH_KEYBOARD_LL hook sees the
/// keystroke first and can swallow it.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private readonly LowLevelKeyboardProc _proc; // kept as a field so GC never collects the delegate
    private IntPtr _hookId;
    private bool _disposed;

    public event Action? HotkeyPressed;

    /// <summary>
    /// While set, every key-down is routed here first (used by the settings
    /// window to record a new hotkey, including Win-combos the OS would
    /// otherwise handle). Return true to swallow the keystroke.
    /// </summary>
    public static Func<uint, bool>? CaptureInterceptor;

    public static bool IsKeyDown(int vk) => IsDown(vk);

    public KeyboardHook()
    {
        _proc = Callback;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hookId == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                if (CaptureInterceptor is { } capture && capture(data.vkCode))
                    return (IntPtr)1;

                var s = Settings.Current;
                bool win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
                if (data.vkCode == s.HotkeyVk
                    && win == s.ModWin
                    && IsDown(VK_SHIFT) == s.ModShift
                    && IsDown(VK_CONTROL) == s.ModCtrl
                    && IsDown(VK_MENU) == s.ModAlt
                    && (s.ModWin || s.ModShift || s.ModCtrl || s.ModAlt))
                {
                    if (s.ModWin)
                    {
                        // The OS saw Win-down but will never see the key we swallow;
                        // without a dummy keystroke it would open the Start menu on Win-up.
                        keybd_event(0xFF, 0, 0, UIntPtr.Zero);
                        keybd_event(0xFF, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }

                    HotkeyPressed?.Invoke();
                    return (IntPtr)1; // swallow
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Windows silently removes LL hooks whose callback ever exceeds the hook
    /// timeout (common after sleep or heavy load) — the hotkey then dies until
    /// restart. Re-arming periodically and on resume keeps it alive.
    /// </summary>
    public void Reinstall()
    {
        if (_disposed) return;
        if (_hookId != IntPtr.Zero)
            UnhookWindowsHookEx(_hookId);
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnhookWindowsHookEx(_hookId);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
