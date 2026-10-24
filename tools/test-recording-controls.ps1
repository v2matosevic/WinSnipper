<# Headless recording controls and selection checks. No installed hooks,
   synthetic input, visible windows, clipboard writes or live task changes. #>
[CmdletBinding()]
param([string]$AppDll, [string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'artifacts/recording-controls' }
$scratch = Join-Path $env:TEMP ('WinSnipper-recording-controls-' + [guid]::NewGuid().ToString('N'))
$harness = Join-Path $scratch 'harness'
New-Item -ItemType Directory -Force -Path $harness,$Out | Out-Null
if (-not $AppDll) {
    & dotnet build (Join-Path $root 'WinSnipper.csproj') -c Release --artifacts-path (Join-Path $scratch 'app') -m:1 -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App build failed' }
    $AppDll = Join-Path $scratch 'app/bin/WinSnipper/release/WinSnipper.dll'
}
$AppDll = (Resolve-Path $AppDll).Path
Copy-Item (Join-Path $root 'app.manifest') (Join-Path $harness 'app.manifest')
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <ApplicationManifest>app.manifest</ApplicationManifest><NoWarn>WFAC010</NoWarn></PropertyGroup>
  <ItemGroup><Using Remove="System.Drawing"/><Using Remove="System.Windows.Forms"/>
    <Reference Include="WinSnipper"><HintPath>$(AppDll)</HintPath></Reference></ItemGroup>
</Project>
'@ | Set-Content (Join-Path $harness 'Checks.csproj')
@'
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSnipper;
using WinSnipper.Recording;

static class Checks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static string Out = "";
    static readonly List<string> Passed = new();
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr hwnd,out uint affinity);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern long GetWindowLongPtr(IntPtr hwnd,int index);
    sealed class HeadlessApp : App { protected override void OnStartup(StartupEventArgs e) { } }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Passed.Add(message); Console.WriteLine("PASS: " + message); }
    static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    static T Named<T>(FrameworkElement root, string name) => (T)root.FindName(name);
    [STAThread]
    static int Main(string[] args)
    {
        Out = args[0];
        var app = new HeadlessApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WinSnipper;component/src/Theme.xaml", UriKind.Relative) });
        try
        {
            Layout(); Hud(); Selector(); EmergencyKeys(); ManagerSelection(); RecorderReadiness(); RecordingRoundTrip();
            File.WriteAllText(Path.Combine(Out, "checks.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = Passed }, new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Layout()
    {
        var type = typeof(App).Assembly.GetType("WinSnipper.RecordingHudLayout")!;
        var place = type.GetMethod("Place", Static)!;
        var clamp = type.GetMethod("Clamp", Static)!;
        var cases = new[] {
            ("fullscreen", new Int32Rect(0,0,1920,1080), new Int32Rect(0,0,1920,1040), 350,84),
            ("negative monitor", new Int32Rect(-2560,-300,2560,1440), new Int32Rect(-2560,-300,2560,1400), 525,126),
            ("stacked monitor", new Int32Rect(1920,-1440,2560,1440), new Int32Rect(1920,-1440,2560,1400), 700,168),
            ("portrait", new Int32Rect(-1080,200,1080,1920), new Int32Rect(-1080,200,1080,1880), 350,84),
            ("mixed-DPI fullscreen", new Int32Rect(3840,500,3840,2160), new Int32Rect(3840,500,3840,2080), 1050,252),
            ("desktop spanning gaps", new Int32Rect(-1920,-1440,7680,3600), new Int32Rect(1920,-1440,2560,1400), 525,126),
            ("small display", new Int32Rect(0,0,320,240), new Int32Rect(0,0,320,200), 350,84),
        };
        var results = new List<object>();
        foreach (var (name, region, work, width, height) in cases)
        {
            var r = (Int32Rect)place.Invoke(null, new object[] {region,work,width,height})!;
            Check(r.Width>0 && r.Height>0 && r.X>=work.X && r.Y>=work.Y && r.X+r.Width<=work.X+work.Width && r.Y+r.Height<=work.Y+work.Height, name+": controls entirely in real monitor work area");
            var moved = (Int32Rect)clamp.Invoke(null,new object[]{new Int32Rect(work.X-9000,work.Y+9000,width,height),work})!;
            Check(moved.X>=work.X && moved.Y>=work.Y && moved.X+moved.Width<=work.X+work.Width && moved.Y+moved.Height<=work.Y+work.Height,name+": dragged controls clamped to monitor");
            results.Add(new { name, region=region.ToString(), work=work.ToString(), controls=r.ToString() });
        }
        var outside = (Int32Rect)place.Invoke(null,new object[]{new Int32Rect(400,300,640,360),new Int32Rect(0,0,1920,1040),350,84})!;
        Check(outside.Y>=672,"region capture keeps controls outside pixels when space exists");
        File.WriteAllText(Path.Combine(Out,"layouts.json"),System.Text.Json.JsonSerializer.Serialize(results,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }
    static void Hud()
    {
        var hud = new RecordingHud(new Int32Rect(0,0,1920,1080),()=>TimeSpan.FromSeconds(83),true);
        int stopped=0, pauses=0;
        hud.StopRequested += ()=>stopped++;
        hud.PauseToggled += paused=>{pauses++;hud.SetPaused(paused);};
        var pause=Named<Button>(hud,"PauseBtn"); var stop=Named<Button>(hud,"StopBtn");
        Render((FrameworkElement)hud.Content,"recording.png");
        pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Named<TextBlock>(hud,"PauseText").Text=="Resume" && Named<TextBlock>(hud,"StateText").Text=="Paused","pause button updates state and resume label");
        Render((FrameworkElement)hud.Content,"paused.png");
        pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(pauses==2 && stopped==1 && Named<TextBlock>(hud,"StateText").Text=="Recording","resume and stop route exactly once");
        hud.Close();

        // EnsureHandle never shows this HUD. The four strip HWNDs lie strictly
        // outside the entire physical desktop bounds, so none are visible.
        bool supportsExclusion=true;
        var native=new RecordingHud(ScreenCapture.VirtualScreenBounds(),()=>TimeSpan.Zero,true,()=>supportsExclusion);
        var hwnd=new WindowInteropHelper(native).EnsureHandle();
        Check(!native.IsVisible&&(GetWindowLongPtr(hwnd,-20)&0x08000000)!=0,"native HUD is nonactivating and remains hidden during verification");
        Check(GetWindowRect(hwnd,out var r)&&System.Windows.Forms.Screen.AllScreens.Any(s=>s.WorkingArea.Contains(new System.Drawing.Rectangle(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top))),"native fullscreen HUD bounds fit one actual monitor work area");
        Check(GetWindowDisplayAffinity(hwnd,out var affinity)&&affinity==0x11&&!native.ControlsMayBeRecorded,"WGC mode excludes embedded HUD on native window");
        supportsExclusion=false;
        var fallback=Task.Run(native.PrepareCompatibilityCapture);
        Check(fallback.Wait(TimeSpan.FromSeconds(3))&&GetWindowDisplayAffinity(hwnd,out affinity)&&affinity==0,"recorder worker clears native affinity while UI thread is blocked, without a dispatcher handoff");
        native.SetCaptureMode(true); // Deliberately stale UI update after the backend changed.
        Check(GetWindowDisplayAffinity(hwnd,out affinity)&&affinity==0,"stale UI update cannot restore embedded protection after backend fallback");
        native.SetCaptureMode(false);
        Check(GetWindowDisplayAffinity(hwnd,out affinity)&&affinity==0&&native.ControlsMayBeRecorded,"compatibility fallback clears embedded protection before black pixels can be captured");
        native.Close();
    }
    static BitmapSource Image()
    {
        var visual=new DrawingVisual(); using(var draw=visual.RenderOpen()) {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(30,38,54)),null,new Rect(0,0,1280,720));
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(45,61,83)),null,new Rect(100,140,960,460));
        }
        var bitmap=new RenderTargetBitmap(1280,720,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
    static void Selector()
    {
        var selector=(SnipOverlay)typeof(SnipOverlay).GetMethod("PrepareScreenshot",Static)!.Invoke(null,null)!;
        Call(selector,"SetScreenshot",Image(),new Int32Rect(0,0,1280,720));
        Call(selector,"SetRecordingSelection");
        Check(!selector.AllowsTransparency,"recording selection uses opaque frozen preview, not virtual-screen layered transparency");
        var root=(FrameworkElement)selector.Content;root.Measure(new Size(1280,720));root.Arrange(new Rect(0,0,1280,720));
        Named<Border>(selector,"ModeBar").Margin=new Thickness(190,20,0,0);
        Set(selector,"_dragging",true);Set(selector,"_startPx",new Point(100,140));
        for(int i=0;i<1000;i++) {Set(selector,"_curPx",new Point(200+i/2,250+i/4));Call(selector,"QueueVisuals");}
        Check(Field<bool>(selector,"_visualsQueued") && Named<TextBlock>(selector,"SizeText").Text=="","1000 pointer updates queue one render without synchronous layout");
        Call(selector,"RenderPendingVisuals",null,EventArgs.Empty);
        Check(!Field<bool>(selector,"_visualsQueued") && Named<TextBlock>(selector,"SizeText").Text=="599 × 359","render uses latest physical coordinates and detaches callback");
        Render(root,"recording-selection.png",1280,720);
        Call(selector,"QueueVisuals");Call(selector,"ClearScreenshot");
        Check(!Field<bool>(selector,"_visualsQueued") && Named<System.Windows.Controls.Image>(selector,"ScreenImage").Source is null,"selector cleanup releases pixels and pending render callback");
        selector.Close();
    }
    static void EmergencyKeys()
    {
        var matches=typeof(KeyboardHook).GetMethod("MatchesRecordingControl",Static)!;
        bool Match(uint vk,bool win,bool shift,bool ctrl,bool alt)=>(bool)matches.Invoke(null,new object[]{vk,win,shift,ctrl,alt})!;
        KeyboardHook.RecordingActive=false;
        Check(!Match(0x7B,false,true,true,false),"emergency shortcuts do not steal keys when idle");
        KeyboardHook.RecordingActive=true;
        Check(Match(0x79,false,true,true,false)&&Match(0x7B,false,true,true,false)&&!Match(0x7B,true,true,true,false)&&!Match(0x7B,false,true,false,false),"pause/stop match exact Ctrl+Shift combos without Windows key");
        var hook=(KeyboardHook)RuntimeHelpers.GetUninitializedObject(typeof(KeyboardHook));
        Set(hook,"_swallowed",new bool[256]);
        var type=typeof(App).Assembly.GetType("WinSnipper.HotkeyDispatch")!;
        using var pauseEntered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var stopDone=new ManualResetEventSlim();
        int caller=Environment.CurrentManagedThreadId,worker=caller,pauseCount=0,stopCount=0;
        var dispatch=Activator.CreateInstance(type,Private|BindingFlags.Public,null,new object[]{(Action<uint,uint,bool>)((_,_,pause)=>{
            worker=Environment.CurrentManagedThreadId;
            if(pause){Interlocked.Increment(ref pauseCount);pauseEntered.Set();release.Wait(TimeSpan.FromSeconds(3));}
            else {Interlocked.Increment(ref stopCount);stopDone.Set();}
        })},null)!;
        Set(hook,"_controlDispatch",dispatch);Call(dispatch,"Start");
        IntPtr data=Marshal.AllocHGlobal(24);
        try {
            Call(hook,"DispatchControl",(uint)0x79,(uint)1,true);
            Check(pauseEntered.Wait(TimeSpan.FromSeconds(3))&&worker!=caller,"pause dispatch stays off native hook thread even with blocked subscriber");
            Marshal.WriteInt32(data,0,0x79);
            bool swallowed=true;
            for(int i=0;i<50;i++) swallowed &= (IntPtr)Call(hook,"Callback",0,(IntPtr)0x100,data)! == (IntPtr)1;
            swallowed &= (IntPtr)Call(hook,"Callback",0,(IntPtr)0x101,data)! == (IntPtr)1;
            Check(swallowed&&pauseCount==1,"emergency key repeat/release swallowed without repeated pause");
            Call(hook,"DispatchControl",(uint)0x7B,(uint)2,false);release.Set();
            Check(stopDone.Wait(TimeSpan.FromSeconds(3))&&stopCount==1,"stop remains queued separately from a blocked pause subscriber");
        } finally {release.Set();((IDisposable)dispatch).Dispose();Marshal.FreeHGlobal(data);KeyboardHook.RecordingActive=false;}
    }
    static void ManagerSelection()
    {
        var image=Image();var pending=new TaskCompletionSource<(BitmapSource,Int32Rect)>(TaskCreationOptions.RunContinuationsAsynchronously);
        int selections=0;string? error=null;
        var manager=(RecordingManager)Activator.CreateInstance(typeof(RecordingManager),Private,null,new object[]{
            (Func<PerformanceTrace,Task<(BitmapSource,Int32Rect)>>)(_=>pending.Task),
            (Func<SnipOverlay,bool>)(overlay=>{selections++;typeof(SnipOverlay).GetProperty("SelectionPx")!.SetValue(overlay,new Int32Rect(2,2,4,4));return true;})},null)!;
        manager.OnError=message=>error=message;
        var first=manager.ToggleAsync(new PerformanceTrace("recording-check"));
        Check(!first.IsCompleted&&SnipOverlay.IsOpen,"background recording preview reserves selector before capture completes");
        var repeat=manager.ToggleAsync(new PerformanceTrace("recording-repeat-check"));
        Check(repeat.IsCompletedSuccessfully&&selections==0,"repeat recording request cannot stack selectors");
        pending.SetResult((image,new Int32Rect(0,0,1280,720)));PumpUntil(()=>first.IsCompleted);first.GetAwaiter().GetResult();
        Check(selections==1&&error?.Contains("too small")==true&&!manager.IsRecording&&!SnipOverlay.IsOpen,"accepted coordinates survive pixel cleanup; invalid region reports error and releases selector");
        var prepared=Field<SnipOverlay>(manager,"_prepared");
        Check(!prepared.AllowsTransparency&&Named<System.Windows.Controls.Image>(prepared,"ScreenImage").Source is null,"cached recording selector is opaque and retains no screenshot while idle");
        prepared.Close();
    }
    static void RecorderReadiness()
    {
        var recorder=new ScreenRecorder(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"missing","bad.mp4"),new Int32Rect(0,0,64,64),15,false);
        recorder.Start();
        var ready=(Task<bool>)typeof(ScreenRecorder).GetProperty("CaptureReady",Private)!.GetValue(recorder)!;
        PumpUntil(()=>ready.IsCompleted,15000);
        var stopped=recorder.StopAsync();PumpUntil(()=>stopped.IsCompleted,15000);
        Check(recorder.Error is not null&&ready.IsCompletedSuccessfully&&stopped.Result is null,"encoder startup failure resolves readiness and stop without stranding controls");
        var paused=new ScreenRecorder("unused.mp4",new Int32Rect(0,0,64,64),15,false);
        var clock=Field<Stopwatch>(paused,"_clock");clock.Start();paused.SetPaused(true);
        var before=paused.Elapsed;Thread.Sleep(100);var after=paused.Elapsed;paused.SetPaused(false);
        Check(Math.Abs((after-before).TotalMilliseconds)<20&&paused.Elapsed<clock.Elapsed-TimeSpan.FromMilliseconds(60),"paused time is excluded when recording resumes");
    }
    static void RecordingRoundTrip()
    {
        // Like the existing reliability fixture: a small private real capture,
        // no HUD or windows on the desktop, no clipboard or user capture files.
        string fixture=Path.Combine(Path.GetTempPath(),"WinSnipper-recording-roundtrip-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string path=Path.Combine(fixture,"recording.mp4");
        var recorder=new ScreenRecorder(path,new Int32Rect(0,0,160,80),15,false);
        recorder.Start();
        var ready=(Task<bool>)typeof(ScreenRecorder).GetProperty("CaptureReady",Private)!.GetValue(recorder)!;
        PumpUntil(()=>ready.IsCompleted,15000);
        PumpUntil(()=>recorder.Elapsed.TotalMilliseconds>=200||recorder.Error is not null,15000);
        recorder.SetPaused(true);Thread.Sleep(100);recorder.SetPaused(false);
        PumpUntil(()=>recorder.Elapsed.TotalMilliseconds>=400||recorder.Error is not null,15000);
        var active=recorder.Elapsed;
        var stopped=recorder.StopAsync();PumpUntil(()=>stopped.IsCompleted,15000);
        Check(recorder.Error is null&&stopped.Result is {IsFrozen:true}&&new FileInfo(path).Length>500,"real pause/resume/stop finalizes a usable recording and frozen thumbnail");
        var (frames,duration)=VideoThumbnails.Extract(path,1,80);
        Check(frames.Count==1&&Math.Abs((duration-active).TotalMilliseconds)<300,"finished MP4 decodes and duration matches active time without the pause");
    }
    static void PumpUntil(Func<bool> done,int timeout=8000)
    {
        var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};
        timer.Tick+=(_,_)=>{if(done()||watch.ElapsedMilliseconds>timeout)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
        if(!done())throw new Exception("Timed out");
    }
    static void Render(FrameworkElement visual,string name,int width=0,int height=0)
    {
        visual.Measure(new Size(width>0?width:double.PositiveInfinity,height>0?height:double.PositiveInfinity));
        width=width>0?width:(int)Math.Ceiling(visual.DesiredSize.Width);height=height>0?height:(int)Math.Ceiling(visual.DesiredSize.Height);
        visual.Arrange(new Rect(0,0,width,height));visual.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(Out,name));png.Save(file);
    }
}
'@ | Set-Content (Join-Path $harness 'Program.cs')
& dotnet run --project (Join-Path $harness 'Checks.csproj') -c Release "-p:AppDll=$AppDll" -- $Out
if ($LASTEXITCODE -ne 0) { throw 'Recording controls checks failed' }
Write-Host "Recording controls evidence: $Out"
