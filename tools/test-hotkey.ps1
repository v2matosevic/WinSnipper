<# Headless regression checks. No installed hooks, synthetic input, visible windows or clipboard writes.
   The selector render check draws real windows that DWM cloaks: never on screen, never activated. #>
[CmdletBinding()]
param([string]$AppDll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('WinSnipper-hotkey-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
if (-not $AppDll) {
    & dotnet build (Join-Path $root 'WinSnipper.csproj') -c Release --artifacts-path (Join-Path $scratch 'app') -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
    $AppDll = Join-Path $scratch 'app/bin/WinSnipper/release/WinSnipper.dll'
}
$AppDll = (Resolve-Path $AppDll).Path
$harness = Join-Path $scratch 'harness'
New-Item -ItemType Directory -Path $harness | Out-Null
Copy-Item (Join-Path $root 'app.manifest') (Join-Path $harness 'app.manifest')
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <ApplicationManifest>app.manifest</ApplicationManifest><NoWarn>WFAC010</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Using Remove="System.Drawing"/><Using Remove="System.Windows.Forms"/>
    <Reference Include="WinSnipper"><HintPath>$(AppDll)</HintPath></Reference>
  </ItemGroup>
</Project>
'@ | Set-Content (Join-Path $harness 'Checks.csproj')
@'
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSnipper;

static class Checks
{
    sealed class HeadlessApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int informationClass, ref int information, int size);
    [DllImport("ntdll.dll")]
    static extern int NtSetInformationProcess(IntPtr process, int informationClass, ref int information, int size);
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static object? Call(object instance, string name, params object[] args) =>
        (instance is App ? typeof(App) : instance.GetType()).GetMethod(name, Instance)!.Invoke(instance, args);
    static object? CallStatic(Type type, string name, params object[] args) =>
        type.GetMethod(name, Static)!.Invoke(null, args);

    [STAThread]
    static void Main()
    {
        // Skip the constructor: the regression test must never install a hook
        // on the maintainer's desktop. Exercise the real dispatch/release code.
        var hook = (KeyboardHook)RuntimeHelpers.GetUninitializedObject(typeof(KeyboardHook));
        typeof(KeyboardHook).GetField("_swallowed", Instance)!.SetValue(hook, new bool[256]);
        var dispatchType = typeof(App).Assembly.GetType("WinSnipper.HotkeyDispatch")!;
        var dispatch = Activator.CreateInstance(dispatchType, Instance | BindingFlags.Public, null,
            new object[] { (Action<uint, uint, bool>)((time, receipt, recording) => {
                var eventField = typeof(KeyboardHook).GetField(recording ? "RecordHotkeyPressed" : "HotkeyPressed", Instance)!;
                ((Action<uint, uint>?)eventField.GetValue(hook))?.Invoke(time, receipt);
            }) }, null)!;
        typeof(KeyboardHook).GetField("_dispatch", Instance)!.SetValue(hook, dispatch);
        Call(dispatch, "Start");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        int caller = Environment.CurrentManagedThreadId, subscriber = caller, count = 0;
        uint received = 0;
        hook.HotkeyPressed += (time, receipt) => {
            subscriber = Environment.CurrentManagedThreadId;
            received = time;
            Interlocked.Increment(ref count);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            finished.Set();
        };
        IntPtr data = Marshal.AllocHGlobal(24);
        try
        {
            Call(hook, "Dispatch", (uint)0x53, (uint)1234, false);
            Assert(entered.Wait(TimeSpan.FromSeconds(3)), "subscriber did not run");
            Assert(subscriber != caller && !finished.IsSet, "subscriber blocked the hook caller");
            Marshal.WriteInt32(data, 0, 0x53);
            for (int i = 0; i < 20; i++)
                Assert((IntPtr)Call(hook, "Callback", 0, (IntPtr)0x100, data)! == (IntPtr)1, "repeat escaped");
            Assert(count == 1, "repeat dispatched another capture");
            Assert((IntPtr)Call(hook, "Callback", 0, (IntPtr)0x101, data)! == (IntPtr)1, "release escaped");
            var swallowed = (bool[])typeof(KeyboardHook).GetField("_swallowed", Instance)!.GetValue(hook)!;
            Assert(!swallowed[0x53], "release did not reset key");
            Assert(received == 1234, "input timestamp lost");
        }
        finally { release.Set(); finished.Wait(TimeSpan.FromSeconds(3)); Marshal.FreeHGlobal(data); }
        Console.WriteLine("PASS: blocked subscriber stays off caller; repeats/release swallowed; timestamp preserved");

        using var recDone = new ManualResetEventSlim();
        hook.RecordHotkeyPressed += (_, _) => recDone.Set();
        Call(hook, "Dispatch", (uint)0x44, (uint)5678, true);
        Assert(recDone.Wait(TimeSpan.FromSeconds(3)), "record dispatch missing");
        Assert(count == 1, "record routed to screenshot");
        Console.WriteLine("PASS: recording dispatch stays separate");

        // A scheduler/host may launch Below Normal with Low I/O and memory
        // priority 2 (Task Scheduler's default). Startup must restore all three.
        using (var self = Process.GetCurrentProcess())
        {
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
            int lowMemory = 2, lowIo = 1;
            SetProcessInformation(self.Handle, 0, ref lowMemory, 4);
            NtSetInformationProcess(self.Handle, 33, ref lowIo, 4);
            Assert(Responsiveness.Describe() == "memory-priority=2 io-priority=1", "could not simulate a scheduled-task launch");
            Responsiveness.Initialize();
            self.Refresh(); // Process caches properties changed through another handle.
            Assert(self.PriorityClass == ProcessPriorityClass.Normal, "startup retained background process priority");
            Assert(Responsiveness.Describe() == "memory-priority=5 io-priority=2", "startup retained low memory or I/O priority");
        }
        Console.WriteLine("PASS: startup normalizes CPU, memory and I/O priority from a scheduled-task launch");

        // Simulate timer expiry without waiting 30 seconds or leaving priority raised.
        using (var old = Responsiveness.Interactive())
        {
            int generation = (int)typeof(Responsiveness).GetField("_generation", Static)!.GetValue(null)!;
            CallStatic(typeof(Responsiveness), "Expire", generation);
            using var current = Responsiveness.Interactive();
            old.Dispose();
            CallStatic(typeof(Responsiveness), "Expire", generation);
            Assert((int)typeof(Responsiveness).GetField("_depth", Static)!.GetValue(null)! == 1,
                "expired scope/timer cancelled the new boost");
        }
        Assert((int)typeof(Responsiveness).GetField("_depth", Static)!.GetValue(null)! == 0, "boost leaked");
        Console.WriteLine("PASS: expired scopes/timers cannot cancel a later boost");

        var app = new HeadlessApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/WinSnipper;component/src/Theme.xaml", UriKind.Relative) });
        int requests = 0;
        var frame = new DispatcherFrame();
        Func<PerformanceTrace, Task> capture = _ => { requests++; frame.Continue = false; return Task.CompletedTask; };
        Call(app, "QueueCapture", "snip", (uint)Environment.TickCount, capture, null!);
        Call(app, "QueueCapture", "snip", (uint)Environment.TickCount, capture, null!);
        Dispatcher.PushFrame(frame);
        Assert(requests == 1, "duplicate pending capture queued");
        Assert((int)typeof(App).GetField("_snipPending", Instance)!.GetValue(app)! == 0, "pending capture never reset");
        frame = new DispatcherFrame();
        Call(app, "QueueCapture", "snip", (uint)Environment.TickCount, capture, null!);
        Dispatcher.PushFrame(frame);
        Assert(requests == 2, "next capture was blocked");
        Console.WriteLine("PASS: pending requests coalesced; next completed capture allowed");
        // The pending flag and boost must last until asynchronous capture ends.
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        frame = new DispatcherFrame();
        Func<PerformanceTrace, Task> asyncCapture = _ => { requests++; frame.Continue = false; return delayed.Task; };
        Call(app, "QueueCapture", "snip", (uint)Environment.TickCount, asyncCapture, null!);
        Dispatcher.PushFrame(frame);
        Call(app, "QueueCapture", "snip", (uint)Environment.TickCount, asyncCapture, null!);
        Assert((int)typeof(App).GetField("_snipPending", Instance)!.GetValue(app)! == 1, "async pending flag released early");
        Assert((int)typeof(Responsiveness).GetField("_depth", Static)!.GetValue(null)! > 0, "async boost released early");
        delayed.SetResult();
        PumpUntil(() => (int)typeof(App).GetField("_snipPending", Instance)!.GetValue(app)! == 0);
        Assert(requests == 3, "duplicate async request escaped coalescing");
        Console.WriteLine("PASS: asynchronous requests retain pending flag and boost until completion");
        var watch = Stopwatch.StartNew();
        var prepared = (SnipOverlay)CallStatic(typeof(SnipOverlay), "PrepareScreenshot")!;
        double prepareMs = watch.Elapsed.TotalMilliseconds;
        Assert(!SnipOverlay.IsOpen, "unshown prepared window blocks snips");
        var bitmap = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgr32, null, new byte[320 * 180 * 4], 320 * 4);
        bitmap.Freeze();
        watch.Restart();
        Call(prepared, "SetScreenshot", bitmap, new Int32Rect(-320, 0, 320, 180));
        double attachMs = watch.Elapsed.TotalMilliseconds;
        Assert((Int32Rect)typeof(SnipOverlay).GetField("_vs", Instance)!.GetValue(prepared)! == new Int32Rect(-320, 0, 320, 180),
            "prepared overlay used stale monitor bounds");
        var fresh = new SnipOverlay(bitmap, new Int32Rect(-320, 0, 320, 180));
        Assert(Render(prepared).SequenceEqual(Render(fresh)), "prepared overlay differs from fresh overlay");
        var hwnd = new System.Windows.Interop.WindowInteropHelper(prepared).Handle;
        Assert(hwnd != IntPtr.Zero, "prepared overlay has no hidden HWND");
        Call(prepared, "ClearScreenshot");
        Assert(!prepared.IsVisible && !SnipOverlay.IsOpen, "idle hidden HWND blocks capture");
        var reservation = (IDisposable)CallStatic(typeof(SnipOverlay), "TryReserveCapture")!;
        Assert(SnipOverlay.IsOpen && CallStatic(typeof(SnipOverlay), "TryReserveCapture") is null, "capture reservation allows overlap");
        reservation.Dispose();
        Assert(!SnipOverlay.IsOpen, "reservation was not released");
        Call(prepared, "SetScreenshot", bitmap, new Int32Rect(-320, 0, 320, 180));
        Assert(new System.Windows.Interop.WindowInteropHelper(prepared).Handle == hwnd, "screenshot reattachment replaced HWND");
        Call(prepared, "ClearScreenshot");
        prepared.Close(); fresh.Close();
        Assert(!SnipOverlay.IsOpen, "closed unshown overlay blocks snips");
        var manager = new SnipManager();
        manager.PrepareOverlay();
        var first = typeof(SnipManager).GetField("_prepared", Instance)!.GetValue(manager);
        manager.PrepareOverlay();
        Assert(ReferenceEquals(first, typeof(SnipManager).GetField("_prepared", Instance)!.GetValue(manager)), "idle preparation stacks windows");
        ((SnipOverlay)first!).Close();
        Console.WriteLine($"PASS: prepared/fresh pixels identical; bounds refreshed; IsOpen clear; one cached overlay. Prepare={prepareMs:F1}ms attach={attachMs:F1}ms");
        CheckModalReuse(bitmap);
        CheckCaptureWorker();
        CheckCaptureFailure();
        CheckActualCapture();
        CheckSelectorShowsCurrentPixels();
        ((IDisposable)dispatch).Dispose();
        app.Shutdown();
    }

    static void PumpUntil(Func<bool> done)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var deadline = Stopwatch.StartNew();
        timer.Tick += (_, _) => { if (done() || deadline.Elapsed.TotalSeconds > 5) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert(done(), "dispatcher check timed out");
    }

    static void CheckCaptureWorker()
    {
        var workerType = typeof(App).Assembly.GetType("WinSnipper.CaptureWorker`1")!.MakeGenericType(typeof((BitmapSource, Int32Rect)));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0, caller = Environment.CurrentManagedThreadId, captureThread = caller;
        var worker = Activator.CreateInstance(workerType, Instance, null,
            new object[] { (Func<(BitmapSource, Int32Rect)>)(() => {
                captureThread = Environment.CurrentManagedThreadId;
                if (Interlocked.Increment(ref calls) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); throw new IOException("injected capture fault"); }
                var image = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr32, null, new byte[256], 32);
                image.Freeze();
                return (image, new Int32Rect(0, 0, 8, 8));
            }) }, null)!;
        try
        {
            var first = (Task<(BitmapSource, Int32Rect)>)Call(worker, "Enqueue", new object[] { null! })!;
            Assert(entered.Wait(TimeSpan.FromSeconds(3)), "capture worker did not enter");
            Assert(captureThread != caller && !first.IsCompleted, "capture ran on caller/UI thread");
            var second = (Task<(BitmapSource, Int32Rect)>)Call(worker, "Enqueue", new object[] { null! })!;
            var overflow = (Task<(BitmapSource, Int32Rect)>)Call(worker, "Enqueue", new object[] { null! })!;
            Assert(overflow.IsFaulted, "capture queue grew without bound");
            release.Set();
            PumpUntil(() => first.IsCompleted && second.IsCompleted);
            Assert(first.IsFaulted && second.Result.Item1.IsFrozen, "capture fault killed worker or unfrozen pixels escaped");
            var weak = CaptureWeakReference(worker);
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            Assert(!weak.TryGetTarget(out _), "idle capture worker retained its last bitmap");
            Console.WriteLine("PASS: blocked capture stays off UI; queue bounded; fault observable; next frozen capture succeeds");
            Console.WriteLine("PASS: idle capture worker does not retain last completed bitmap");
        }
        finally { release.Set(); Call(worker, "Complete"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference<BitmapSource> CaptureWeakReference(object worker)
    {
        var task = (Task<(BitmapSource, Int32Rect)>)Call(worker, "Enqueue", new object[] { null! })!;
        PumpUntil(() => task.IsCompleted);
        return new WeakReference<BitmapSource>(task.GetAwaiter().GetResult().Item1);
    }

    static void CheckModalReuse(BitmapSource bitmap)
    {
        // A real modal HWND far outside the desktop, never activated. No input
        // is sent. Check Hide's modal return behavior, not a mocked window.
        var overlay = (SnipOverlay)CallStatic(typeof(SnipOverlay), "PrepareScreenshot")!;
        overlay.ShowActivated = false;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
        for (int i = 0; i < 3; i++)
        {
            var bounds = new Int32Rect(-30000 - i * 320, -30000, 320, 180);
            if (i > 0)
            {
                var modeType = typeof(SnipOverlay).GetNestedType("PickMode", BindingFlags.NonPublic)!;
                Call(overlay, "SetMode", Enum.Parse(modeType, "Window"));
            }
            Call(overlay, "SetScreenshot", bitmap, bounds);
            Assert(typeof(SnipOverlay).GetField("_mode", Instance)!.GetValue(overlay)!.ToString() == "Region",
                "previous pick mode survived screenshot reset");
            Assert(typeof(SnipOverlay).GetField("_windows", Instance)!.GetValue(overlay) is null,
                "previous window cache survived screenshot reset");
            var trace = new PerformanceTrace("offscreen-reuse-check");
            trace.TrackWindow(overlay);
            bool accepted = i != 1;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) => {
                timer.Stop();
                Assert(overlay.IsVisible && SnipOverlay.IsOpen, "modal selection is not reserved");
                Assert(GetWindowRect(hwnd, out var rect) && rect.Left == bounds.X && rect.Top == bounds.Y
                    && rect.Right - rect.Left == bounds.Width && rect.Bottom - rect.Top == bounds.Height,
                    "retained HWND used stale physical monitor bounds");
                var steps = (List<string>)typeof(PerformanceTrace).GetField("_steps", Instance)!.GetValue(trace)!;
                Assert(steps.Any(step => step.StartsWith("rendered=")), "reshown window did not report its per-show render");
                if (accepted) Call(overlay, "FinishWith", 10, 20, 40, 30);
                else overlay.Close(); // Alt+F4/WM_CLOSE follows Closing.
            };
            timer.Start();
            bool result = (bool)Call(overlay, "ShowScreenshotSelection")!;
            timer.Stop();
            trace.Finish("finished-before-render");
            Assert(result == accepted, "reused modal acceptance/cancellation was lost");
            Assert(!overlay.IsVisible && !SnipOverlay.IsOpen, "finished overlay is still visible/reserved");
            Assert(new System.Windows.Interop.WindowInteropHelper(overlay).Handle == hwnd, "modal selection recreated HWND");
            Assert(typeof(PerformanceTrace).GetField("_detachWindow", Instance)!.GetValue(trace) is null, "trace handlers leaked on retained window");
            if (accepted) Assert(overlay.SelectionPx == new Int32Rect(10, 20, 40, 30), "reused selection pixels changed");
            Call(overlay, "ClearScreenshot");
            var image = (System.Windows.Controls.Image)typeof(SnipOverlay).GetField("ScreenImage", Instance)!.GetValue(overlay)!;
            Assert(ReferenceEquals(image.Source, bitmap) && overlay.SelectionPx is null, "idle overlay detached its capture surface or kept its selection");
        }
        overlay.Close();
        Console.WriteLine("PASS: offscreen modal accept/cancel/accept reuse same HWND; every show traced; idle selection cleared with surface attached; trace handlers detached");
    }

    static void CheckCaptureFailure()
    {
        var delayed = new TaskCompletionSource<(BitmapSource, Int32Rect)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = (SnipManager)Activator.CreateInstance(typeof(SnipManager), Instance, null,
            new object[] { (Func<PerformanceTrace, Task<(BitmapSource, Int32Rect)>>)(_ => delayed.Task) }, null)!;
        var capture = manager.StartSnipAsync(new PerformanceTrace("injected-delayed-capture"));
        Assert(SnipOverlay.IsOpen && !capture.IsCompleted, "pending capture did not reserve selection");
        var repeat = manager.StartSnipAsync(new PerformanceTrace("injected-repeat"));
        Assert(repeat.IsCompletedSuccessfully, "repeat queued behind delayed capture");
        delayed.SetException(new IOException("injected capture failure"));
        PumpUntil(() => capture.IsCompleted);
        Assert(capture.IsFaulted && !SnipOverlay.IsOpen, "capture failure lost error or leaked selection reservation");
        Assert(!(bool)typeof(SnipManager).GetField("_active", Instance)!.GetValue(manager)!, "capture failure left manager active");
        Assert((int)typeof(Responsiveness).GetField("_depth", Static)!.GetValue(null)! == 0, "capture failure leaked priority boost");
        _ = capture.Exception; // The failure is deliberately observed.
        Console.WriteLine("PASS: delayed manager capture reserves selection; repeats rejected; failure releases manager/reservation/boost");
    }

    static (BitmapSource, Int32Rect) ActualCapture()
    {
        var workerType = typeof(App).Assembly.GetType("WinSnipper.CaptureWorker")!;
        var task = (Task<(BitmapSource, Int32Rect)>)CallStatic(workerType, "CaptureAsync", new object[] { null! })!;
        PumpUntil(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    static void CheckActualCapture()
    {
        var (image, bounds) = ActualCapture();
        Assert(image.PixelWidth == bounds.Width && image.PixelHeight == bounds.Height,
            "real background capture pixels/bounds invalid");
        Assert(bounds == ScreenCapture.VirtualScreenBounds(), "capture worker used a different DPI/monitor context");
        Assert(!image.IsFrozen && image.CheckAccess(), "shared capture surface is not the UI thread's live bitmap");
        var pixel = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert(pixel[3] == 255, "real background capture pixel was transparent/unreadable on UI");
        Console.WriteLine($"PASS: real dedicated STA capture presents UI-readable shared pixels and physical bounds {bounds}");

        // The regression this surface exists for: every new desktop-sized WPF
        // bitmap costs a full collection, so repeat captures must reuse one.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int gen2 = GC.CollectionCount(2);
        for (int i = 0; i < 4; i++)
        {
            var (again, _) = ActualCapture();
            Assert(ReferenceEquals(again, image), "repeat capture created a new desktop-sized bitmap");
            Thread.Sleep(250);
        }
        Assert(GC.CollectionCount(2) == gen2, $"repeat captures forced {GC.CollectionCount(2) - gen2} full collection(s)");
        Console.WriteLine("PASS: four repeat captures reuse one surface and force no full collection");

        int side = Math.Min(32, Math.Min(bounds.Width, bounds.Height));
        var copy = (BitmapSource)CallStatic(typeof(ScreenCapture), "CopyRegion", image, new Int32Rect(0, 0, side, side))!;
        Assert(copy.IsFrozen && copy.PixelWidth == side && copy.PixelHeight == side && copy.Format == PixelFormats.Bgr32,
            "selection copy is not an owned frozen image");
        var source = new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgr32, null);
        var white = Enumerable.Repeat((byte)255, side * side * 4).ToArray();
        source.WritePixels(new Int32Rect(0, 0, side, side), white, side * 4, 0);
        var owned = (BitmapSource)CallStatic(typeof(ScreenCapture), "CopyRegion", source, new Int32Rect(0, 0, side, side))!;
        source.WritePixels(new Int32Rect(0, 0, side, side), new byte[side * side * 4], side * 4, 0);
        var after = new byte[side * side * 4];
        owned.CopyPixels(after, side * 4, 0);
        Assert(after.All(b => b == 255), "selection copy still reads the source's later pixels");
        Console.WriteLine("PASS: selection copies are frozen and keep their pixels after the surface changes");
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr MapViewOfFile(SafeHandle section, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
    [DllImport("kernel32.dll")]
    static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    static extern uint GetPixel(IntPtr dc, int x, int y);

    static void CheckSelectorShowsCurrentPixels()
    {
        // The selector must show this capture, not the GPU copy of an earlier
        // one: InteropBitmap.Invalidate() only reaches a bitmap that some
        // visual still holds. Only a rendered window shows that (off-screen
        // windows are not rendered), so this one is real and hardware-drawn
        // but cloaked by DWM: nothing appears, no activation, no input.
        const int W = 160, H = 100;
        var bounds = new Int32Rect(100, 100, W, H);
        var section = (SafeHandle)CallStatic(typeof(ScreenCapture), "CreateFileMapping",
            new IntPtr(-1), IntPtr.Zero, 0x04u, 0u, (uint)(W * H * 4), IntPtr.Zero)!;
        IntPtr view = MapViewOfFile(section, 0x2 /* FILE_MAP_WRITE */, 0, 0, UIntPtr.Zero);
        Assert(view != IntPtr.Zero, "test surface could not be mapped");
        var frameType = typeof(ScreenCapture).GetNestedType("SharedFrame", BindingFlags.NonPublic)!;
        var frame = Activator.CreateInstance(frameType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { section, bounds }, null)!;

        SnipOverlay Prepare()
        {
            var overlay = (SnipOverlay)CallStatic(typeof(SnipOverlay), "PrepareScreenshot")!;
            overlay.ShowActivated = false;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            int on = 1;
            Assert(DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref on, 4) == 0
                && DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0,
                "could not cloak the test selector; nothing was shown");
            return overlay;
        }

        string Shown(SnipOverlay overlay, string name, int bgrx)
        {
            Marshal.Copy(Enumerable.Repeat(bgrx, W * H).ToArray(), 0, view, W * H); // the worker's blit
            var shot = (BitmapSource)CallStatic(typeof(ScreenCapture), "PresentShared", frame)!;
            Call(overlay, "SetScreenshot", shot, bounds);
            overlay.Show();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            Assert(DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0, "test selector lost its cloak");
            var shown = Stopwatch.StartNew();
            PumpUntil(() => shown.ElapsedMilliseconds >= 400);
            uint pixel = SampleCenter(hwnd);
            Call(overlay, "Finish", false); // hidden, as after a selection
            Call(overlay, "ClearScreenshot");
            var hidden = Stopwatch.StartNew();
            PumpUntil(() => hidden.ElapsedMilliseconds >= 150);
            int r = (int)(pixel & 0xFF), g = (int)((pixel >> 8) & 0xFF), b = (int)((pixel >> 16) & 0xFF);
            string seen = (r > 100, g > 100, b > 100, r < 50 || g < 50 || b < 50) switch
            {
                (true, false, false, true) => "red",
                (false, true, false, true) => "green",
                (false, false, true, true) => "blue",
                _ => $"rgb({r},{g},{b})",
            };
            Assert(seen == name, $"selector showed {seen} instead of this capture's {name}");
            return seen;
        }

        try
        {
            var first = Prepare();
            Shown(first, "red", 0x00FF0000);
            Shown(first, "blue", 0x000000FF);
            Shown(first, "green", 0x0000FF00);
            // A selector window that is destroyed takes the bitmap off the
            // render thread; its successor must not inherit the old GPU copy.
            // Another window keeps the GPU device (and that copy) alive, as
            // the app's other windows do; without one, WPF drops the device.
            var witness = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 100, Width = 40, Height = 40 };
            var witnessHwnd = new System.Windows.Interop.WindowInteropHelper(witness).EnsureHandle();
            int cloak = 1;
            Assert(DwmSetWindowAttribute(witnessHwnd, 13, ref cloak, 4) == 0
                && DwmGetWindowAttribute(witnessHwnd, 14, out int witnessCloaked, 4) == 0 && witnessCloaked != 0,
                "could not cloak the witness window; nothing was shown");
            witness.Show();
            try
            {
                first.Close();
                var second = Prepare();
                Shown(second, "red", 0x00FF0000);
                Shown(second, "blue", 0x000000FF);
                second.Close();
            }
            finally { witness.Close(); }
        }
        finally { UnmapViewOfFile(view); }
        Console.WriteLine("PASS: a hidden, reshown and replaced selector each show the current capture (cloaked hardware render)");
    }

    static uint SampleCenter(IntPtr hwnd)
    {
        Assert(GetWindowRect(hwnd, out var rect), "test selector has no bounds");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), target = CreateCompatibleBitmap(screen, width, height);
        IntPtr previous = SelectObject(memory, target);
        try
        {
            Assert(PrintWindow(hwnd, memory, 2 /* PW_RENDERFULLCONTENT */), "could not read the test selector's rendered pixels");
            return GetPixel(memory, width / 2, height / 2);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(target);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static byte[] Render(Window window)
    {
        var content = (UIElement)window.Content;
        content.Measure(new Size(320, 180));
        content.Arrange(new Rect(0, 0, 320, 180));
        var image = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var pixels = new byte[320 * 180 * 4];
        image.CopyPixels(pixels, 320 * 4, 0);
        return pixels;
    }
}
'@ | Set-Content (Join-Path $harness 'Program.cs')
& dotnet run --project (Join-Path $harness 'Checks.csproj') -c Release "-p:AppDll=$AppDll"
if ($LASTEXITCODE -ne 0) { throw 'Hotkey/overlay regression checks failed.' }
