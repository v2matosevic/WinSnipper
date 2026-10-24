using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Interop;

namespace WinSnipper;

internal enum CaptureIntent { Screenshot = 1, Recording = 2, Probe = 3 }

/// <summary>Small, current-user IPC endpoint. No paths, scripts, callback URLs or
/// image data cross the elevation boundary, only a bounded capture command.</summary>
internal sealed class CaptureActivation : IDisposable
{
    private const int Ack = 0x57534E50;
    private static readonly IntPtr MessageParent = new(-3); // HWND_MESSAGE
    private static readonly uint Message = RegisterWindowMessage("WinSnipper.Capture.Activate.v1");
    private static readonly SecurityIdentifier UserSid = CurrentUserSid();
    private static string WindowName => "WinSnipper.Capture." + UserSid.Value;
    private readonly HwndSource _source;
    private readonly Action<CaptureIntent> _deliver;
    private readonly HashSet<int> _seen = new();
    private readonly Queue<int> _order = new();

    internal CaptureActivation(Action<CaptureIntent> deliver, string? endpointName = null)
    {
        _deliver = deliver;
        _source = new HwndSource(new HwndSourceParameters(endpointName ?? WindowName)
        { ParentWindow = MessageParent, Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(HandleMessage);
        if (!ChangeWindowMessageFilterEx(_source.Handle, Message, 1, IntPtr.Zero))
        {
            _source.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message != Message) return IntPtr.Zero;
        handled = true;
        long command = wParam.ToInt64(), request = lParam.ToInt64();
        if (command is < 1 or > 3 || request is < 1 or > int.MaxValue) return IntPtr.Zero;
        if (command == (int)CaptureIntent.Probe) return (IntPtr)Ack;
        if (_seen.Add((int)request))
        {
            _order.Enqueue((int)request);
            if (_order.Count > 64) _seen.Remove(_order.Dequeue());
            // Acknowledge before any capture work. Duplicate retries use the
            // same request id and cannot open a second selector.
            _source.Dispatcher.BeginInvoke(() => _deliver((CaptureIntent)command));
        }
        return (IntPtr)Ack;
    }

    internal static bool TrySend(CaptureIntent intent, int requestId, string? endpointName = null)
    {
        var hwnd = FindWindowEx(MessageParent, IntPtr.Zero, null, endpointName ?? WindowName);
        if (hwnd == IntPtr.Zero || !IsOurExecutable(hwnd, out uint pid)) return false;
        // A shell-launched activation process passes its foreground permission
        // to the resident instance. This does not activate anything for Probe.
        AllowSetForegroundWindow(pid);
        return SendMessageTimeout(hwnd, Message, (IntPtr)(int)intent, (IntPtr)requestId,
            0x0003 /* BLOCK | ABORTIFHUNG */, 500, out var result) != IntPtr.Zero && result == (UIntPtr)Ack;
    }

    private static SecurityIdentifier CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    private static bool IsOurExecutable(IntPtr hwnd, out uint pid)
    {
        GetWindowThreadProcessId(hwnd, out pid);
        var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            var path = new StringBuilder(32768); int length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length)
                && StringComparer.OrdinalIgnoreCase.Equals(path.ToString(), Environment.ProcessPath)
                && HasUserSid(process, UserSid);
        }
        finally { CloseHandle(process); }
    }

    private static bool HasUserSid(IntPtr process, SecurityIdentifier expected)
    {
        if (!OpenProcessToken(process, 8 /* TOKEN_QUERY */, out var token)) return false;
        try
        {
            GetTokenInformation(token, 1 /* TokenUser */, IntPtr.Zero, 0, out int size);
            if (size <= 0 || size > 4096) return false;
            var data = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, 1, data, size, out _)) return false;
                return expected.Equals(new SecurityIdentifier(Marshal.ReadIntPtr(data)));
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        finally { CloseHandle(token); }
    }

    internal static bool TryForwardOrStart(CaptureIntent intent)
    {
        int requestId = Random.Shared.Next(1, int.MaxValue);
        if (TrySend(intent, requestId)) return true;
        if (!TryStartSupervised()) return false;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (TrySend(intent, requestId)) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static bool TryStartSupervised()
    {
        object? service = null, folder = null, task = null, definition = null, actions = null, action = null, principal = null, running = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return false;
            service = Activator.CreateInstance(type)!; ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            task = ((dynamic)folder).GetTask("WinSnipper Keep-Alive");
            if (!(bool)((dynamic)task).Enabled) return false;
            definition = ((dynamic)task).Definition;
            principal = ((dynamic)definition).Principal;
            string owner = (string)((dynamic)principal).UserId;
            var sid = owner.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? owner
                : new NTAccount(owner).Translate(typeof(SecurityIdentifier)).Value;
            if (sid != UserSid.Value) return false;
            actions = ((dynamic)definition).Actions;
            if ((int)((dynamic)actions).Count != 1) return false;
            action = ((dynamic)actions).Item(1);
            if (!StringComparer.OrdinalIgnoreCase.Equals((string)((dynamic)action).Path, Environment.ProcessPath)
                || (string)((dynamic)action).Arguments != "--watchdog") return false;
            // An explicit native screenshot request supersedes an earlier tray
            // exit. Background watchdog launches still honor that marker.
            Util.ClearUserQuit();
            running = ((dynamic)task).Run(null);
            return true;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { return false; }
        catch (Exception ex) { Util.LogCrash("Capture activation", ex); return false; }
        finally
        {
            foreach (var item in new[] { running, action, actions, principal, definition, task, folder, service })
                if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }

    public void Dispose() => _source.Dispose();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr data, int length, out int needed);
}
