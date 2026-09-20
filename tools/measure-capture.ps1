<# Measures the old and current capture paths without opening windows or saving screenshots. #>
[CmdletBinding()]
param([string]$BaselineRevision = '423b3c2806113d2ace9de687486923b8ae4c80ab')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('WinSnipper-capture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$legacy = & git -C $root show "${BaselineRevision}:src/ScreenCapture.cs"
if ($LASTEXITCODE -ne 0) { throw 'Could not read baseline capture source.' }
($legacy -join "`n").Replace('class ScreenCapture', 'class LegacyCapture') | Set-Content (Join-Path $scratch 'LegacyCapture.cs')
Copy-Item (Join-Path $root 'src/ScreenCapture.cs') (Join-Path $scratch 'ScreenCapture.cs')
Copy-Item (Join-Path $root 'app.manifest') (Join-Path $scratch 'app.manifest')
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings><ApplicationManifest>app.manifest</ApplicationManifest>
    <NoWarn>WFAC010</NoWarn>
  </PropertyGroup>
  <ItemGroup><Using Remove="System.Drawing"/><Using Remove="System.Windows.Forms"/></ItemGroup>
</Project>
'@ | Set-Content (Join-Path $scratch 'CaptureBench.csproj')
@'
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSnipper;

class Program
{
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
    [STAThread]
    static void Main()
    {
        var oldTimes = new List<double>();
        var newTimes = new List<double>();
        double Run(bool legacy)
        {
            var sw = Stopwatch.StartNew();
            var shot = legacy ? LegacyCapture.CaptureVirtualScreen() : ScreenCapture.CaptureVirtualScreen();
            sw.Stop();
            if (!shot.image.IsFrozen || shot.image.PixelWidth != shot.boundsPx.Width ||
                shot.image.PixelHeight != shot.boundsPx.Height) throw new Exception("Invalid capture dimensions/ownership");
            // Read pixels after the GDI bitmap has been disposed.
            shot.image.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), new byte[4], 4, 0);
            return sw.Elapsed.TotalMilliseconds;
        }
        double oldFirst = Run(true), newFirst = Run(false);
        int handlesBefore = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        for (int i = 0; i < 12; i++)
        {
            // Alternate order to avoid consistently favouring a warm desktop read.
            if (i % 2 == 0) { oldTimes.Add(Run(true)); newTimes.Add(Run(false)); }
            else { newTimes.Add(Run(false)); oldTimes.Add(Run(true)); }
        }
        int handlesAfter = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        if (handlesAfter > handlesBefore) throw new Exception("Capture leaked GDI handles");
        var (image, bounds) = ScreenCapture.CaptureVirtualScreen();
        var crop = new CroppedBitmap(image, new System.Windows.Int32Rect(0, 0, 64, 64));
        crop.Freeze();
        byte[] before = new byte[64 * 64 * 4];
        var opaque = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);
        opaque.CopyPixels(before, 64 * 4, 0);
        for (int i = 3; i < before.Length; i += 4)
            if (before[i] != 255) throw new Exception("Screenshot contains transparent pixels");
        using var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop)); encoder.Save(png); png.Position = 0;
        var decoded = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        byte[] after = new byte[before.Length];
        new FormatConvertedBitmap(decoded.Frames[0], PixelFormats.Bgra32, null, 0).CopyPixels(after, 64 * 4, 0);
        if (!before.SequenceEqual(after)) throw new Exception("PNG round-trip changed pixels");
        // The current capture path keeps its pixels in an unmanaged section, so
        // prove the handle and the memory come back rather than piling up.
        var self = Process.GetCurrentProcess();
        var retention = new List<string>();
        int Settle()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers();
            self.Refresh();
            return self.HandleCount;
        }
        int handlesAtStart = Settle();
        int handlesAtEnd = handlesAtStart;
        foreach (int batch in new[] { 48, 96, 192 })
        {
            for (int i = 0; i < batch; i++) { var (img, _) = ScreenCapture.CaptureVirtualScreen(); GC.KeepAlive(img); }
            handlesAtEnd = Settle();
            retention.Add($"after {batch}: {handlesAtEnd} handles, {self.PrivateMemorySize64 / (1024 * 1024)} MB");
        }
        // A capture that forgot to release its section would show up as one
        // retained handle and ~24 MB per capture; flat across 336 is the pass.
        if (handlesAtEnd - handlesAtStart > 16)
            throw new Exception($"Captures retained handles: {handlesAtStart} -> {handlesAtEnd}");
        long privateMb = self.PrivateMemorySize64 / (1024 * 1024);
        if (privateMb > 400)
            throw new Exception($"Captures retained memory: {privateMb} MB after 336 captures");

        double Median(List<double> values) { values.Sort(); return (values[5] + values[6]) / 2; }
        Console.WriteLine(JsonSerializer.Serialize(new {
            bounds = bounds.ToString(), renderTier = RenderCapability.Tier >> 16,
            baselineFirstMs = oldFirst, currentFirstMs = newFirst,
            baselineMedianMs = Median(oldTimes), currentMedianMs = Median(newTimes),
            baselineSamplesMs = oldTimes, currentSamplesMs = newTimes,
            gdiHandlesBefore = handlesBefore, gdiHandlesAfter = handlesAfter,
            handlesBefore = handlesAtStart, handlesAfter = handlesAtEnd,
            retention, privateMemoryMbAfter = privateMb,
            frozenPixelsCropOpaquePngRoundTrip = "passed"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
'@ | Set-Content (Join-Path $scratch 'Program.cs')
& dotnet run --project (Join-Path $scratch 'CaptureBench.csproj') -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Capture benchmark failed.' }
