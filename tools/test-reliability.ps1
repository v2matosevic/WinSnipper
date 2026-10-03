<# Failure-path checks with synthetic images and fake clipboard writers. No input or clipboard writes. A small private recording verifies the real finalization/error path. #>
[CmdletBinding()]
param([string]$AppDll, [string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('WinSnipper-reliability-checks-' + [guid]::NewGuid().ToString('N'))
$harness = Join-Path $scratch 'harness'
if (-not $Out) { $Out = Join-Path $root 'artifacts/reliability' }
New-Item -ItemType Directory -Force -Path $harness, $Out | Out-Null
if (-not $AppDll) {
    & dotnet build (Join-Path $root 'WinSnipper.csproj') -c Release --artifacts-path (Join-Path $scratch 'app') -m:1 -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
    $AppDll = Join-Path $scratch 'app/bin/WinSnipper/release/WinSnipper.dll'
}
$AppDll = (Resolve-Path $AppDll).Path
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <NoWarn>WFAC010</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Using Remove="System.Drawing"/><Using Remove="System.Windows.Forms"/>
    <Reference Include="WinSnipper"><HintPath>$(AppDll)</HintPath></Reference>
  </ItemGroup>
</Project>
'@ | Set-Content (Join-Path $harness 'Checks.csproj')
@'
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSnipper;
using WinSnipper.Recording;

static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static string Dir = "", Out = "";
    static BitmapSource Image = null!;
    static readonly List<string> Passed = new();
    static readonly Type IO = typeof(Util).Assembly.GetType("WinSnipper.ReliableIO", true)!;

    [STAThread]
    static int Main(string[] args)
    {
        Dir = args[0]; Out = args[1];
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/WinSnipper;component/src/Theme.xaml") });
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) draw.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 160, 80));
        var bitmap = new RenderTargetBitmap(160, 80, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); Image = bitmap;
        int code = 0;
        try
        {
            AtomicWrites(); CaptureNames(); ClipboardRetries(); ThumbnailRecovery(); EditorRecovery(); RecordingFailure();
            File.WriteAllText(Path.Combine(Out, "checks.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = Passed, privateFixture = Dir }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Reliability: {Passed.Count} checks passed. Fixtures: {Dir}");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
        // No close-time clipboard actions or interaction with the installed app.
        Environment.Exit(code);
        return code;
    }

    static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Passed.Add(name); Console.WriteLine("PASS " + name);
    }

    static void AtomicWrites()
    {
        string path = Path.Combine(Dir, "original.png");
        Util.SavePng(Image, path);
        byte[] original = File.ReadAllBytes(path);
        bool failed = false;
        Action<Stream> fullDisk = stream => { stream.Write(new byte[120]); throw new IOException("Simulated disk full"); };
        try { IO.GetMethod("Write")!.Invoke(null, new object[] { path, fullDisk, true }); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException) { failed = true; }
        Check(failed && original.SequenceEqual(File.ReadAllBytes(path)), "failed encode preserves original PNG");
        Check(!Directory.EnumerateFiles(Dir, "*.tmp").Any(), "failed encode removes only its scratch file");
        using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = false;
            try { Util.SavePng(Image, path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        }
        Check(failed && original.SequenceEqual(File.ReadAllBytes(path)), "locked destination preserves original PNG");
        failed = false;
        try { Util.SavePng(Image, path, overwrite: false); } catch (IOException) { failed = true; }
        Check(failed && original.SequenceEqual(File.ReadAllBytes(path)), "new capture cannot overwrite an existing destination");
        string fresh = Path.Combine(Dir, "fresh.png");
        Util.SavePng(Image, fresh, overwrite: false);
        using var file = File.OpenRead(fresh);
        var frame = BitmapFrame.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Check(frame.PixelWidth == 160 && frame.PixelHeight == 80, "successful PNG commit has expected dimensions");
        byte[] expected = new byte[160 * 80 * 4], actual = new byte[expected.Length];
        Image.CopyPixels(expected, 160 * 4, 0); frame.CopyPixels(actual, 160 * 4, 0);
        Check(expected.SequenceEqual(actual), "PNG commit preserves synthetic capture pixels");
    }

    static void CaptureNames()
    {
        string previous = Settings.Current.SaveDir;
        Settings.Current.SaveDir = Dir;
        try
        {
            var manager = new SnipManager();
            var next = typeof(SnipManager).GetMethod("NextSnipPath", Private)!;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 1000; i++) paths.Add((string)next.Invoke(manager, null)!);
            Check(paths.Count == 1000, "rapid capture names remain unique before background saves finish");
        }
        finally { Settings.Current.SaveDir = previous; }
    }

    static void ClipboardRetries()
    {
        int thread = Environment.CurrentManagedThreadId, calls = 0, beats = 0;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        heartbeat.Tick += (_, _) => beats++;
        heartbeat.Start();
        var write = typeof(Util).GetMethod("WriteClipboardAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Action busyThenFree = () => { if (Environment.CurrentManagedThreadId != thread) throw new Exception("wrong clipboard thread"); if (++calls < 4) throw new ExternalException("Clipboard unavailable"); };
        var success = (Task<bool>)write.Invoke(null, new object[] { busyThenFree, (Func<uint>)(() => 0) })!;
        Wait(success);
        Check(success.Result && calls == 4 && beats > 3, "clipboard retries yield to UI and retain STA ownership");
        calls = 0;
        var failure = (Task<bool>)write.Invoke(null, new object[] { (Action)(() => { calls++; throw new ExternalException("Clipboard unavailable"); }), (Func<uint>)(() => 0) })!;
        Wait(failure);
        Check(!failure.Result && calls == 4, "permanent clipboard failure is reported");
        calls = 0; string copied = "";
        var older = (Task<bool>)write.Invoke(null, new object[] { (Action)(() => { calls++; if (calls == 1) throw new ExternalException("busy"); copied = "old"; }), (Func<uint>)(() => 0) })!;
        var newer = (Task<bool>)write.Invoke(null, new object[] { (Action)(() => copied = "new"), (Func<uint>)(() => 0) })!;
        Wait(older); Wait(newer); heartbeat.Stop();
        Check(!older.Result && newer.Result && copied == "new" && calls == 1, "older clipboard retry cannot replace newer app copy");
        uint sequence = 0; calls = 0;
        var superseded = (Task<bool>)write.Invoke(null, new object[] { (Action)(() => { calls++; throw new ExternalException("busy"); }), (Func<uint>)(() => sequence) })!;
        sequence++;
        Wait(superseded);
        Check(!superseded.Result && calls == 1, "clipboard changed by another app cancels stale retry");
    }

    static void ThumbnailRecovery()
    {
        string path = Path.Combine(Dir, "delayed.png");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thumb = new FloatingThumb(path, Image, saving: pending.Task);
        var wait = thumb.EnsureSavedAsync();
        Check(!wait.IsCompleted, "thumbnail handoff waits without blocking UI");
        Wait(wait, 12000);
        Check(!wait.Result && ReferenceEquals(thumb.GetType().GetField("_saving", Private)!.GetValue(thumb), pending.Task), "save timeout retains unfinished task and blocks file handoff");
        Util.SavePng(Image, path); pending.SetResult();
        var complete = thumb.EnsureSavedAsync(); Wait(complete);
        Check(complete.Result, "timed-out save can later finish successfully");
        var fault = new FloatingThumb(Path.Combine(Dir, "retry.png"), Image, saving: Task.FromException(new IOException("disk full")));
        var failed = fault.EnsureSavedAsync(); Wait(failed);
        Check(!failed.Result && ((TextBlock)fault.FindName("DeliveryStatus")).Text.Contains("Not saved"), "failed thumbnail keeps visible recovery reason");
        Render((FrameworkElement)fault.Content, "thumbnail-save-error.png", 220, 210);
        fault.GetType().GetMethod("RetrySave_Click", Private)!.Invoke(fault, new object[] { fault, new RoutedEventArgs() });
        PumpUntil(() => (bool)fault.GetType().GetField("_saved", Private)!.GetValue(fault)!, 5000);
        Check(File.Exists(Path.Combine(Dir, "retry.png")), "thumbnail retry saves its retained image");
        var copied = (Task)fault.GetType().GetMethod("ObserveCopyAsync", Private)!.Invoke(fault, new object[] { Task.FromResult(false) })!; Wait(copied);
        Check(((TextBlock)fault.FindName("DeliveryStatus")).Text.Contains("Not copied"), "clipboard failure keeps thumbnail recoverable");
        Render((FrameworkElement)fault.Content, "thumbnail-copy-error.png", 220, 210);
    }

    static void EditorRecovery()
    {
        string path = Path.Combine(Dir, "editor.png"); Util.SavePng(Image, path);
        byte[] original = File.ReadAllBytes(path);
        var editor = new EditorWindow(path, Image) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        editor.Show();
        var type = editor.GetType();
        type.GetField("_dirty", Private)!.SetValue(editor, true);
        using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            editor.Close();
            PumpUntil(() => ((TextBlock)editor.FindName("StatusMsg")).Text.StartsWith("Not saved"), 5000);
            Check(editor.IsLoaded && (bool)type.GetField("_dirty", Private)!.GetValue(editor)!, "failed close keeps editor and dirty annotations open");
            editor.UpdateLayout();
            var shot = new RenderTargetBitmap((int)Math.Ceiling(editor.ActualWidth), (int)Math.Ceiling(editor.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            shot.Render(editor);
            SaveRender(shot, "editor-save-error.png");
        }
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "editor failure leaves previous capture intact");
        string alternate = Path.Combine(Dir, "editor-recovered.png");
        var save = (Task)type.GetMethod("SaveAsync", Private)!.Invoke(editor, new object[] { alternate })!; Wait(save);
        Check(File.Exists(alternate) && (string)type.GetField("_path", Private)!.GetValue(editor)! == alternate && !(bool)type.GetField("_dirty", Private)!.GetValue(editor)!, "save elsewhere recovers editor and updates path after success");
        editor.Hide();
    }

    static void RecordingFailure()
    {
        string path = Path.Combine(Dir, "failed-recording.mp4");
        var recorder = new ScreenRecorder(path, new Int32Rect(0, 0, 160, 80), 15, false);
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        typeof(ScreenRecorder).GetProperty("BeforeFinalize", Private)!.SetValue(recorder, (Action)(() => { entered.Set(); finish.Wait(TimeSpan.FromSeconds(5)); throw new IOException("Injected encoder finalize failure"); }));
        recorder.Start();
        PumpUntil(() => recorder.Elapsed >= TimeSpan.FromMilliseconds(200) || recorder.Error is not null, 15000);
        var manager = new RecordingManager(); string? error = null; int errors = 0;
        manager.OnError = message => { error = message; errors++; };
        typeof(RecordingManager).GetField("_recorder", Private)!.SetValue(manager, recorder);
        var firstStop = manager.StopAsync();
        var secondStop = manager.StopAsync();
        PumpUntil(() => entered.IsSet, 5000);
        Check(entered.IsSet && !firstStop.IsCompleted && !secondStop.IsCompleted, "concurrent stop waits for the same recording completion");
        finish.Set();
        Wait(firstStop, 15000); Wait(secondStop, 15000);
        Check(recorder.Error is not null && error?.Contains("Injected encoder finalize failure") == true && errors == 1, "actual recorder finalize failure reaches manager once");
        var result = recorder.StopAsync(); Wait(result);
        Check(result.Result is null && !manager.IsRecording && File.Exists(path), "failed recording cannot produce success thumbnail and retains partial file");
        Wait(manager.StopAsync());
        Check(errors == 1, "repeated stop does not duplicate recording error");
    }

    static void Wait(Task task, int timeout = 8000)
    {
        PumpUntil(() => task.IsCompleted, timeout);
        if (!task.IsCompleted) throw new Exception("operation timed out");
        task.GetAwaiter().GetResult();
    }
    static void PumpUntil(Func<bool> done, int timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < timeout)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
    }
    static void Render(FrameworkElement content, string name, int width, int height)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = content.DesiredSize;
        width = Math.Max(1, (int)Math.Ceiling(desired.Width)); height = Math.Max(1, (int)Math.Ceiling(desired.Height));
        content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        SaveRender(image, name);
    }
    static void SaveRender(BitmapSource image, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(Out, name)); encoder.Save(output);
    }
}
'@ | Set-Content (Join-Path $harness 'Program.cs')
& dotnet run --project (Join-Path $harness 'Checks.csproj') -c Release "-p:AppDll=$AppDll" -- $scratch $Out
if ($LASTEXITCODE -ne 0) { throw 'Reliability checks failed.' }
Write-Host "Reliability renders and results: $Out"
