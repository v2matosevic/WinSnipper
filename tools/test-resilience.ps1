<#
Checks for the things that made a long-running WinSnipper slow or broken (2026-11-21):
the clipboard image path, the locked capture surface, memory release after use, the
runtime-removed detector, the UI stall recorder and editor windows being collected.

Nothing appears on screen and no input is sent. The clipboard checks run on a private
window station, which has its own clipboard, so the interactive clipboard is never touched.

  pwsh -NoProfile -File tools\test-resilience.ps1          # all checks
  pwsh -NoProfile -File tools\test-resilience.ps1 -Perf    # also time WPF's Clipboard.SetImage against ours
#>
[CmdletBinding()]
param([string]$AppDll, [switch]$Perf)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('WinSnipper-resilience-' + [guid]::NewGuid().ToString('N'))
$harness = Join-Path $scratch 'harness'
New-Item -ItemType Directory -Force -Path $harness | Out-Null
if (-not $AppDll) {
    & dotnet build (Join-Path $root 'WinSnipper.csproj') -c Release -p:EnableOcr=true --artifacts-path (Join-Path $scratch 'app') -m:1 -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
    $AppDll = Join-Path $scratch 'app/bin/WinSnipper/release/WinSnipper.dll'
}
$AppDll = (Resolve-Path $AppDll).Path
Copy-Item (Join-Path $root 'app.manifest') (Join-Path $harness 'app.manifest')
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable>
    <NoWarn>WFAC010;CS8632</NoWarn>
    <ApplicationManifest>app.manifest</ApplicationManifest>
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
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSnipper;

static class Program
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Assembly App = typeof(Util).Assembly;
    static int failures, passed;

    [MTAThread]
    static int Main(string[] args)
    {
        bool clipboard = args.Contains("clipboard");
        if (clipboard)
        {
            // A window station owns its clipboard. Everything below runs on a
            // private one; only administrators may name a station, so it is unnamed.
            IntPtr station = CreateWindowStationW(null, 0, 0x37F, IntPtr.Zero);
            if (station == IntPtr.Zero || !SetProcessWindowStation(station)) return Abort("could not create a private window station");
            if (CreateDesktopW("Default", null, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero) == IntPtr.Zero) return Abort("could not create a private desktop");
        }
        int code = 2;
        // The STA thread starts after the station switch, so its windows land on the private desktop.
        var sta = new Thread(() => code = clipboard ? ClipboardMain(args.Contains("perf")) : ChecksMain());
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        return code;
    }

    static int Finish(string what)
    {
        Console.WriteLine(failures == 0 ? $"{what}: {passed} checks passed." : $"{what}: {failures} FAILED, {passed} passed.");
        Environment.Exit(failures == 0 ? 0 : 1);
        return 0;
    }

    static int Abort(string why) { Console.Error.WriteLine("ABORT: " + why + " (" + Marshal.GetLastWin32Error() + ")"); return 2; }

    static void Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        if (ok) passed++; else failures++;
    }

    static Type T(string name) => App.GetType("WinSnipper." + name, true);
    static object Call(string type, string method, params object[] a) => T(type).GetMethod(method, Any).Invoke(null, a);
    static object Get(string type, string property) => T(type).GetProperty(property, Any).GetValue(null);
    static void Set(string type, string property, object value) => T(type).GetProperty(property, Any).SetValue(null, value);

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    static void Await(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    // ---------- general checks ----------

    static int ChecksMain()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/WinSnipper;component/src/Theme.xaml") });
        try
        {
            CaptureSurfaceStaysInMemory();
            RuntimeRemovalIsNoticed();
            MemoryIsReleasedWhenIdle();
            UiStallsAreRecorded();
            EditorsAreCollected();
            WarmupBuildsTheEditor();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); failures++; }
        return Finish("Resilience");
    }

    static void CaptureSurfaceStaysInMemory()
    {
        Console.WriteLine("capture surface");
        Call("ScreenCapture", "BlitShared");
        string pin = (string)Call("Residency", "Pin");
        Check(pin.StartsWith("residency min="), $"minimum working set applied ({pin})");
        string locked = (string)Call("ScreenCapture", "KeepSharedResident");
        Check(locked.StartsWith("capture-surface locked="), $"surface locked ({locked})");
        IntPtr view = (IntPtr)Get("ScreenCapture", "SharedView");
        var vs = ScreenCapture.VirtualScreenBounds();
        long bytes = (long)vs.Width * 4 * vs.Height;

        // Control: ordinary memory leaves the working set when it is emptied.
        IntPtr plain = VirtualAlloc(IntPtr.Zero, (nuint)(1 << 20), 0x3000, 0x04);
        Marshal.WriteByte(plain, 1);
        Check(InWorkingSet(plain), "control page is resident before the working set is emptied");
        EmptyWorkingSet(GetCurrentProcess());
        Check(!InWorkingSet(plain), "control page was evicted (the eviction really happened)");
        Check(InWorkingSet(view) && InWorkingSet(view + (nint)(bytes / 2)) && InWorkingSet(view + (nint)(bytes - 4096)),
            "locked surface is still resident at its start, middle and end");

        // A later capture reuses the same locked surface.
        Call("ScreenCapture", "BlitShared");
        Check((bool)Get("ScreenCapture", "SharedIsResident") && (IntPtr)Get("ScreenCapture", "SharedView") == view, "next capture reuses the locked surface");
    }

    static bool InWorkingSet(IntPtr address)
    {
        var info = new WorkingSetInfo { Address = address };
        if (!QueryWorkingSetEx(GetCurrentProcess(), ref info, Marshal.SizeOf<WorkingSetInfo>())) throw new Exception("QueryWorkingSetEx failed");
        return (info.Attributes.ToInt64() & 1) != 0;
    }

    static void RuntimeRemovalIsNoticed()
    {
        Console.WriteLine("runtime removal");
        var probes = (string[])Call("RuntimeHealth", "FrameworkProbes");
        Check(probes.Length == 2 && probes.All(File.Exists), $"the two framework files this process runs from are found ({string.Join(" | ", probes.Select(Path.GetFileName))})");
        Check((bool)Call("RuntimeHealth", "Intact", new object[] { probes }), "an installed runtime reads as intact");

        string probe = Path.Combine(Path.GetTempPath(), "ws-runtime-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "x");
        int raised = 0;
        Call("RuntimeHealth", "Start", new[] { probe }, (Action)(() => Interlocked.Increment(ref raised)), TimeSpan.FromMilliseconds(100));
        Thread.Sleep(450);
        Check(Volatile.Read(ref raised) == 0, "nothing is raised while the file exists");
        File.Delete(probe);
        Thread.Sleep(450);
        Check(Volatile.Read(ref raised) >= 2, $"removal is raised, and raised again until handled ({raised} times)");
        Call("RuntimeHealth", "Start", new[] { Path.GetTempPath() + "never" + Guid.NewGuid() }, (Action)(() => { }), TimeSpan.FromHours(1)); // replace the fast timer
        Call("RuntimeHealth", "Preload");
        Check(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Accessibility"), "the assembly behind tooltips and popups is loaded ahead of need");
    }

    static void MemoryIsReleasedWhenIdle()
    {
        Console.WriteLine("memory release");
        var lines = new List<string>();
        int asked = 0;
        Set("MemoryTrim", "Log", (Action<string>)(line => { lock (lines) lines.Add(line); }));
        Set("MemoryTrim", "Delay", TimeSpan.FromMilliseconds(120));
        Call("MemoryTrim", "Initialize", (Func<bool>)(() => Interlocked.Increment(ref asked) <= 2)); // busy twice, then idle

        // Garbage only a full collection frees: dead large arrays.
        long before;
        using (var self = Process.GetCurrentProcess()) before = self.PrivateMemorySize64;
        for (int i = 0; i < 6; i++) GC.KeepAlive(new byte[48 << 20]);
        Call("MemoryTrim", "Schedule");
        Thread.Sleep(1200);
        lock (lines)
        {
            Check(asked >= 3, $"waits while work is in progress, then runs (asked {asked} times)");
            Check(lines.Count == 1 && lines[0].StartsWith("memory-trim private="), $"one result line ({string.Join(" / ", lines)})");
        }
        long after;
        using (var self = Process.GetCurrentProcess()) after = self.PrivateMemorySize64;
        Check(after < before + (64L << 20), $"the dead buffers were given back (private {before >> 20} MB before, {after >> 20} MB after)");
    }

    static void UiStallsAreRecorded()
    {
        Console.WriteLine("ui stall recorder");
        var lines = new List<string>();
        Call("UiWatchdog", "Start", Dispatcher.CurrentDispatcher, (Action<string>)(line => { lock (lines) lines.Add(line); }));
        Pump(900);
        lock (lines) Check(lines.Count == 0, "a responsive UI thread records nothing");
        var scope = (IDisposable)Call("UiWatchdog", "Enter", "test-stage");
        Thread.Sleep(2600); // the UI thread stops answering
        scope.Dispose();
        Pump(1300);
        lock (lines)
        {
            string line = lines.FirstOrDefault(l => l.StartsWith("ui-stall duration=")) ?? "";
            int ms = line.Length > 0 ? int.Parse(line.Split('=')[1].Split('m')[0]) : 0;
            Check(ms is >= 2000 and <= 4500 && line.Contains("stage=test-stage"), $"the stall is recorded with its length and stage ({(line.Length > 0 ? line : string.Join(" / ", lines))})");
        }
    }

    static void EditorsAreCollected()
    {
        Console.WriteLine("editor windows");
        var alive = new List<WeakReference>();
        for (int i = 0; i < 6; i++) alive.Add(OpenAndCloseEditor(i));
        Pump(200);
        Call("MemoryTrim", "Collect");
        Pump(100);
        Check(alive.All(r => !r.IsAlive), $"closed editors are collected ({alive.Count(r => r.IsAlive)} of {alive.Count} still alive)");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference OpenAndCloseEditor(int seed)
    {
        var win = new EditorWindow(Path.Combine(Path.GetTempPath(), $"ws-check-{seed}.png"), Noise(1280, 720, PixelFormats.Bgr32, seed));
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -32000; win.Top = -32000; win.ShowActivated = false; win.ShowInTaskbar = false;
        win.Show();
        Pump(150);
        win.Close(); // not edited: closes without saving or touching the clipboard
        return new WeakReference(win);
    }

    static void WarmupBuildsTheEditor()
    {
        Console.WriteLine("warm-up");
        int before = Application.Current.Windows.Count;
        Warmup.Prime();
        Check(Application.Current.Windows.Count == before, "warm-up leaves no window behind");
    }

    // ---------- clipboard, on a private window station ----------

    const uint CF_BITMAP = 2, CF_DIB = 8, CF_DIBV5 = 17;

    static int ClipboardMain(bool perf)
    {
        var name = new char[128];
        GetUserObjectInformationW(GetProcessWindowStation(), 2, name, name.Length * 2, out _);
        string station = new string(name).TrimEnd('\0');
        if (station.Length == 0 || station.Equals("WinSta0", StringComparison.OrdinalIgnoreCase)) return Abort($"not on a private window station ('{station}')");
        // WSF_VISIBLE marks the interactive station, the only one whose clipboard the user sees.
        var flags = new int[3];
        if (!GetUserObjectInformationW(GetProcessWindowStation(), 1, flags, 12, out _) || (flags[2] & 1) != 0) return Abort("this window station is interactive; refusing to write");
        Console.WriteLine($"clipboard (private window station {station})");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { Await(ClipboardChecks(perf)); }
        catch (Exception ex) { Console.Error.WriteLine(ex); failures++; }
        if (OpenClipboard(IntPtr.Zero)) { EmptyClipboard(); CloseClipboard(); } // leave the private clipboard as it was found
        return Finish("Clipboard");
    }

    static async Task ClipboardChecks(bool perf)
    {
        await RoundTrip("bgr32 odd size 337x211", Noise(337, 211, PixelFormats.Bgr32, 1));
        await RoundTrip("bgr32 1x1", Noise(1, 1, PixelFormats.Bgr32, 2));
        await RoundTrip("editor composite (pbgra32) 640x360", Rendered(640, 360));
        await RoundTrip("bgr24 123x77", Noise(123, 77, PixelFormats.Bgr24, 3));
        await RoundTrip("full desktop 5760x1080", Noise(5760, 1080, PixelFormats.Bgr32, 4));

        var first = Util.SetClipboardAsync(Noise(5760, 1080, PixelFormats.Bgr32, 5));
        var second = Util.SetClipboardAsync(Noise(64, 48, PixelFormats.Bgr32, 6));
        bool firstPlaced = await first, secondPlaced = await second;
        var (w, h, _) = ReadDib();
        Check(secondPlaced && !firstPlaced && w == 64 && h == 48, $"a later copy is not overwritten by an earlier, larger one (clipboard holds {w}x{h})");

        if (!perf) return;
        var desktop = Noise(5760, 1080, PixelFormats.Bgr32, 7);
        foreach (string which in new[] { "WPF Clipboard.SetImage", "WinSnipper CF_DIB" })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetTotalAllocatedBytes(true);
            var clock = Stopwatch.StartNew();
            long blocked;
            if (which.StartsWith("WPF")) { Clipboard.SetImage(desktop); blocked = clock.ElapsedMilliseconds; }
            else { var task = Util.SetClipboardAsync(desktop); blocked = clock.ElapsedMilliseconds; await task; }
            Console.WriteLine($"  perf {which,-22} 5760x1080: UI thread blocked {blocked} ms, total {clock.ElapsedMilliseconds} ms, managed allocations {(GC.GetTotalAllocatedBytes(true) - allocated) >> 20} MB");
        }
    }

    static async Task RoundTrip(string label, BitmapSource image)
    {
        bool placed = await Util.SetClipboardAsync(image);
        if (!placed) { Check(false, $"{label}: placed on the clipboard"); return; }
        var (w, h, pixels) = ReadDib();
        var expected = new byte[image.PixelWidth * 4 * image.PixelHeight];
        new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0).CopyPixels(expected, image.PixelWidth * 4, 0);
        int mismatches = w == image.PixelWidth && h == image.PixelHeight ? 0 : -1;
        for (int y = 0; mismatches == 0 && y < h; y++)
        {
            int src = y * w * 4, dst = (h - 1 - y) * w * 4; // CF_DIB rows run bottom-up
            for (int x = 0; x < w * 4; x += 4)
                if (pixels[dst + x] != expected[src + x] || pixels[dst + x + 1] != expected[src + x + 1]
                    || pixels[dst + x + 2] != expected[src + x + 2] || pixels[dst + x + 3] != 0xFF) { mismatches++; break; }
        }
        bool derived = IsClipboardFormatAvailable(CF_BITMAP) && IsClipboardFormatAvailable(CF_DIBV5);
        BitmapSource viaWpf = null;
        try { viaWpf = Clipboard.GetImage(); } catch { }
        Check(mismatches == 0 && derived && viaWpf is { } read && read.PixelWidth == w && read.PixelHeight == h,
            $"{label}: every pixel round-trips opaque, Windows derives CF_BITMAP/CF_DIBV5, a WPF reader gets {viaWpf?.PixelWidth}x{viaWpf?.PixelHeight}");
    }

    static (int W, int H, byte[] Pixels) ReadDib()
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new Exception("OpenClipboard failed");
        try
        {
            IntPtr handle = GetClipboardData(CF_DIB);
            if (handle == IntPtr.Zero) throw new Exception("no CF_DIB on the clipboard");
            IntPtr p = GlobalLock(handle);
            try
            {
                int size = Marshal.ReadInt32(p, 0), w = Marshal.ReadInt32(p, 4), h = Marshal.ReadInt32(p, 8);
                if (size != 40 || Marshal.ReadInt16(p, 14) != 32 || Marshal.ReadInt32(p, 16) != 0 || h <= 0) throw new Exception("unexpected DIB header");
                var pixels = new byte[w * 4 * h];
                Marshal.Copy(p + 40, pixels, 0, pixels.Length);
                return (w, h, pixels);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    static BitmapSource Noise(int w, int h, PixelFormat format, int seed)
    {
        int stride = (w * format.BitsPerPixel + 7) / 8;
        var px = new byte[stride * h];
        new Random(seed).NextBytes(px);
        var b = BitmapSource.Create(w, h, 96, 96, format, null, px, stride);
        b.Freeze();
        return b;
    }

    static BitmapSource Rendered(int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, w, h));
            dc.DrawEllipse(Brushes.OrangeRed, new Pen(Brushes.White, 3), new Point(w / 2, h / 2), w / 4, h / 3);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    [StructLayout(LayoutKind.Sequential)] struct WorkingSetInfo { public IntPtr Address; public IntPtr Attributes; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowStationW(string name, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateDesktopW(string name, string device, IntPtr devmode, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(IntPtr obj, int index, [Out] char[] info, int length, out int needed);
    [DllImport("user32.dll")] static extern bool GetUserObjectInformationW(IntPtr obj, int index, [Out] int[] info, int length, out int needed);
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAlloc(IntPtr address, nuint bytes, uint type, uint protect);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("psapi.dll")] static extern bool QueryWorkingSetEx(IntPtr process, ref WorkingSetInfo info, int size);
}
'@ | Set-Content (Join-Path $harness 'Program.cs')

$out = Join-Path $scratch 'bin'
& dotnet build (Join-Path $harness 'Checks.csproj') -c Release "-p:AppDll=$AppDll" -o $out -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw 'Harness build failed.' }
$exe = Join-Path $out 'Checks.exe'
$failed = 0
& $exe checks; if ($LASTEXITCODE -ne 0) { $failed++ }
$clipArgs = @('clipboard'); if ($Perf) { $clipArgs += 'perf' }
& $exe @clipArgs; if ($LASTEXITCODE -ne 0) { $failed++ }
Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
if ($failed) { throw "Resilience checks failed ($failed group(s))." }
