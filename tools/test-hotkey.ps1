<# Headless regression checks. No installed hooks, synthetic input, visible windows or clipboard writes. #>
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
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Using Remove="System.Drawing"/><Using Remove="System.Windows.Forms"/>
    <Reference Include="WinSnipper"><HintPath>$(AppDll)</HintPath></Reference>
  </ItemGroup>
</Project>
'@ | Set-Content (Join-Path $harness 'Checks.csproj')
@'
using System.Diagnostics;
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
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static object? Call(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, Instance)!.Invoke(instance, args);
    static object? CallStatic(Type type, string name, params object[] args) =>
        type.GetMethod(name, Static)!.Invoke(null, args);

    [STAThread]
    static void Main()
    {
        // Skip the constructor: the regression test must never install a hook
        // on the maintainer's desktop. Exercise the real dispatch/release code.
        var hook = (KeyboardHook)RuntimeHelpers.GetUninitializedObject(typeof(KeyboardHook));
        typeof(KeyboardHook).GetField("_swallowed", Instance)!.SetValue(hook, new bool[256]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        int caller = Environment.CurrentManagedThreadId, subscriber = caller, count = 0;
        uint received = 0;
        hook.HotkeyPressed += time => {
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
        hook.RecordHotkeyPressed += _ => recDone.Set();
        Call(hook, "Dispatch", (uint)0x44, (uint)5678, true);
        Assert(recDone.Wait(TimeSpan.FromSeconds(3)), "record dispatch missing");
        Assert(count == 1, "record routed to screenshot");
        Console.WriteLine("PASS: recording dispatch stays separate");

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

        var app = new App();
        app.InitializeComponent();
        int requests = 0;
        var frame = new DispatcherFrame();
        Action<PerformanceTrace> capture = _ => { requests++; frame.Continue = false; };
        Call(app, "QueueCapture", "snip", (uint)Environment.TickCount, capture);
        Call(app, "QueueCapture", "snip", (uint)Environment.TickCount, capture);
        Dispatcher.PushFrame(frame);
        Assert(requests == 1, "duplicate pending capture queued");
        Assert((int)typeof(App).GetField("_snipPending", Instance)!.GetValue(app)! == 0, "pending capture never reset");
        frame = new DispatcherFrame();
        Call(app, "QueueCapture", "snip", (uint)Environment.TickCount, capture);
        Dispatcher.PushFrame(frame);
        Assert(requests == 2, "next capture was blocked");
        Console.WriteLine("PASS: pending requests coalesced; next completed capture allowed");
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
        prepared.Close(); fresh.Close();
        Assert(!SnipOverlay.IsOpen, "closed unshown overlay blocks snips");
        var manager = new SnipManager();
        manager.PrepareOverlay();
        var first = typeof(SnipManager).GetField("_prepared", Instance)!.GetValue(manager);
        manager.PrepareOverlay();
        Assert(ReferenceEquals(first, typeof(SnipManager).GetField("_prepared", Instance)!.GetValue(manager)), "idle preparation stacks windows");
        ((SnipOverlay)first!).Close();
        Console.WriteLine($"PASS: prepared/fresh pixels identical; bounds refreshed; IsOpen clear; one cached overlay. Prepare={prepareMs:F1}ms attach={attachMs:F1}ms");
        app.Shutdown();
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
