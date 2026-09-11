<#
Renders the screenshot editor and the trim window to PNGs without opening
anything on screen or sending input: an isolated build of the app, a throwaway
WPF harness that shows the windows far off-screen (never activated, never in the
taskbar) and captures them with RenderTargetBitmap. Content is a synthetic
dashboard and a synthetic screen recording, never a real capture.

  pwsh -NoProfile -File tools\ui-shots.ps1            # every editor/trim state -> artifacts\ui-shots
  pwsh -NoProfile -File tools\ui-shots.ps1 -Readme    # also refresh docs\images\editor.png, trim.png

Needs ffmpeg on PATH (it encodes the sample recording).
#>
[CmdletBinding()]
param(
    [string]$Out,
    [switch]$Readme
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'artifacts\ui-shots' }
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'ffmpeg must be on PATH; it encodes the sample recording.' }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('WinSnipper-uishots-' + [guid]::NewGuid().ToString('N'))
$harness = Join-Path $scratch 'harness' # own folder: the SDK globs every .cs below a project
New-Item -ItemType Directory -Force -Path $harness, $Out | Out-Null

# The app itself, built apart from bin/obj and dist so a running copy or a peer's build is never touched.
& dotnet build (Join-Path $root 'WinSnipper.csproj') -c Release -p:EnableOcr=true --artifacts-path (Join-Path $scratch 'app') -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw 'WinSnipper build failed.' }
$dll = Join-Path $scratch 'app\bin\WinSnipper\release\WinSnipper.dll'

@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable>
    <NoWarn>WFAC010;CS8632</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Using Remove="System.Drawing"/><Using Remove="System.Windows.Forms"/>
    <Reference Include="WinSnipper"><HintPath>$(WsDll)</HintPath></Reference>
  </ItemGroup>
</Project>
'@ | Set-Content (Join-Path $harness 'UiShots.csproj')

@'
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

// 96 DPI everywhere, so renders are identical whichever monitor scaling this machine runs.
[assembly: System.Windows.Media.DisableDpiAwareness]

static class Program
{
    // DeclaredOnly: Window has internal members with the same names (UpdateTitle).
    const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static string Out = "";

    [STAThread]
    static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/WinSnipper;component/src/Theme.xaml"),
        });
        int code = 0;
        try
        {
            if (args[0] == "frames") Frames(args[1]);
            else { Out = args[1]; Directory.CreateDirectory(Out); Editor(); Trim(args[2]); }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
        // Exit without closing: the editor's close-time save would touch the clipboard.
        Environment.Exit(code);
        return code;
    }

    // ---------- sample recording: 12 s of the mock app with a chart drawing in ----------

    static void Frames(string dir)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 120; i++)
        {
            double t = i / 119.0;
            var cursor = new Point(360 + 700 * t, 520 - 260 * Math.Sin(t * Math.PI));
            Save(Mock(1280, 720, dark: false, progress: 0.15 + 0.85 * t, cursor: cursor), Path.Combine(dir, $"f{i:000}.png"));
        }
    }

    // ---------- editor ----------

    static void Editor()
    {
        var light = Mock(1280, 760, dark: false);

        for (int i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            var w = New("WinSnipper.EditorWindow", Path.Combine(Out, $"timing{i}.png"), light);
            long ctor = sw.ElapsedMilliseconds, rendered = -1;
            w.ContentRendered += (_, _) => rendered = sw.ElapsedMilliseconds;
            Show(w);
            PumpUntil(() => rendered >= 0, 5000);
            Console.WriteLine($"editor open #{i}: ctor {ctor} ms, first render {rendered} ms");
            Clean(w);
            w.Hide();
        }

        var e = New("WinSnipper.EditorWindow", Path.Combine(Out, "Snip 2026-09-11 14-32-07.png"), light);
        Show(e);
        Pump(400);
        Shot(e, "editor.png");
        Annotate(e);
        Pump(250);
        Shot(e, "editor-annotated.png");
        Shot(e, "editor-annotated-2x.png", 2);
        Shot(e, "readme-editor.png", rounded: true);
        Crop(e, new Rect(180, 120, 620, 380));
        Pump(200);
        Shot(e, "editor-crop.png");
        Clean(e);

        var dark = New("WinSnipper.EditorWindow", Path.Combine(Out, "dark.png"), Mock(900, 560, dark: true));
        Show(dark);
        Pump(400);
        Shot(dark, "editor-dark-compact.png");
        if (((Popup)dark.FindName("StylePopup")).Child is FrameworkElement fly)
            ShotDetached(fly, "editor-flyout.png"); // a Popup is its own HWND: opening it would land on a real screen
        Clean(dark);

        var tiny = New("WinSnipper.EditorWindow", Path.Combine(Out, "tiny.png"), Mock(320, 180, dark: false));
        Show(tiny);
        Pump(400);
        Shot(tiny, "editor-min.png");
        Clean(tiny);
    }

    static void Annotate(Window w)
    {
        var t = w.GetType();
        var ink = (Canvas)w.FindName("Ink");
        var undo = (Stack<UIElement>)t.GetField("_undo", Priv)!.GetValue(w)!;
        var red = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        void Add(UIElement el) { ink.Children.Add(el); undo.Push(el); }

        // what a real review looks like: box the number, point at the cause, hide the address
        var box = new Shapes.Rectangle { Stroke = red, StrokeThickness = 3, RadiusX = 2, RadiusY = 2, Width = 340, Height = 110 };
        Canvas.SetLeft(box, 922); Canvas.SetTop(box, 109);
        Add(box);
        var geo = (Geometry)t.GetMethod("BuildArrow", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { new Point(690, 628), new Point(826, 492), 3.0 })!;
        Add(new Shapes.Path { Stroke = red, Fill = red, StrokeThickness = 2.25, StrokeLineJoin = PenLineJoin.Round, Data = geo });
        t.GetMethod("PlaceBadge", Priv)!.Invoke(w, new object[] { new Point(922, 109) });
        t.GetMethod("PlaceBadge", Priv)!.Invoke(w, new object[] { new Point(520, 640) });
        t.GetMethod("StartText", Priv)!.Invoke(w, new object[] { new Point(540, 626) });
        ((TextBox)t.GetField("_editBox", Priv)!.GetValue(w)!).Text = "Churn starts here";
        t.GetMethod("CommitText", Priv)!.Invoke(w, null);
        t.GetMethod("ApplyPixelate", Priv)!.Invoke(w, new object[] { new Rect(1052, 12, 160, 30) });
        t.GetField("_dirty", Priv)!.SetValue(w, true);
        t.GetMethod("UpdateTitle", Priv)!.Invoke(w, null);
        t.GetMethod("UpdateUndoButtons", Priv)!.Invoke(w, null);
    }

    static void Crop(Window w, Rect r)
    {
        var t = w.GetType();
        t.GetMethod("SelectTool", Priv)!.Invoke(w, new object[] { w.FindName("BtnCrop") });
        ((UIElement)w.FindName("CropLayer")).Visibility = Visibility.Visible;
        ((UIElement)w.FindName("CropSel")).Visibility = Visibility.Visible;
        t.GetMethod("UpdateCropVisuals", Priv)!.Invoke(w, new object[] { r });
        t.GetField("_pendingCrop", Priv)!.SetValue(w, (Rect?)r);
        t.GetMethod("PlaceCropActions", Priv)!.Invoke(w, new object[] { r });
        ((UIElement)w.FindName("CropActions")).Visibility = Visibility.Visible;
    }

    static void Clean(Window w) => w.GetType().GetField("_dirty", Priv)?.SetValue(w, false);

    // ---------- trim ----------

    static void Trim(string video)
    {
        var w = New("WinSnipper.TrimWindow", video);
        Show(w);
        Pump(3500); // media open + filmstrip decode
        Shot(w, "trim.png");

        var t = w.GetType();
        t.GetField("_trimStart", Priv)!.SetValue(w, TimeSpan.FromSeconds(2.4));
        t.GetField("_trimEnd", Priv)!.SetValue(w, TimeSpan.FromSeconds(8.7));
        t.GetMethod("SeekTo", Priv)!.Invoke(w, new object[] { TimeSpan.FromSeconds(6.2), true });
        t.GetField("_hasPlayed", Priv)?.SetValue(w, true); // hide the first-run play button
        t.GetMethod("UpdatePlayOverlay", Priv)?.Invoke(w, null);
        t.GetMethod("UpdateTimeline", Priv)!.Invoke(w, null);
        Pump(700);
        Shot(w, "trim-trimmed.png");

        var drag = t.GetNestedType("DragTarget", BindingFlags.NonPublic)!;
        t.GetField("_drag", Priv)!.SetValue(w, Enum.Parse(drag, "End"));
        t.GetMethod("ShowTimeBadge", Priv)!.Invoke(w, new object[] { TimeSpan.FromSeconds(8.7) });
        Pump(150);
        Shot(w, "trim-drag.png");
        Shot(w, "readme-trim.png", rounded: true);
        t.GetField("_drag", Priv)!.SetValue(w, Enum.Parse(drag, "None"));
        ((UIElement)w.FindName("TimeBadge")).Visibility = Visibility.Collapsed;

        t.GetMethod("ShowError", Priv)!.Invoke(w, new object[] { "Trimming failed: The process cannot access the file because it is being used by another process." });
        w.Width = 640; w.Height = 460;
        Pump(900);
        t.GetMethod("UpdateTimeline", Priv)!.Invoke(w, null);
        Pump(300);
        Shot(w, "trim-min-error.png");
    }

    // ---------- helpers ----------

    static Window New(string type, params object[] args) =>
        (Window)Activator.CreateInstance(Assembly.Load("WinSnipper").GetType(type, true)!, args)!;

    static void Show(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Show();
    }

    static void Shot(Window w, string name, double scale = 1, bool rounded = false)
    {
        w.UpdateLayout();
        double width = w.ActualWidth, height = w.ActualHeight;
        var rtb = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        if (!rounded)
        {
            rtb.Render(w);
        }
        else
        {
            // Windows 11 corners and a hairline, on transparency so it sits on GitHub's light and dark themes.
            var frame = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            frame.Render(w);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var r = new Rect(0, 0, width, height);
                dc.PushClip(new RectangleGeometry(r, 8, 8));
                dc.DrawImage(frame, r);
                dc.Pop();
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x3D, 0x80, 0x80, 0x80)), 1),
                    new Rect(0.5, 0.5, width - 1, height - 1), 8, 8);
            }
            rtb.Render(dv);
        }
        Save(rtb, Path.Combine(Out, name));
        Console.WriteLine($"  {name}");
    }

    static void ShotDetached(FrameworkElement el, string name)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        el.Arrange(new Rect(el.DesiredSize));
        el.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth), (int)Math.Ceiling(el.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        rtb.Render(dv);
        Save(rtb, Path.Combine(Out, name));
        Console.WriteLine($"  {name}");
    }

    static void Save(BitmapSource img, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    static void PumpUntil(Func<bool> done, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < maxMs) Pump(5);
    }

    // A plausible app: header, sidebar, KPI cards, a revenue chart.
    static BitmapSource Mock(int w, int h, bool dark, double progress = 1, Point? cursor = null)
    {
        var bg = dark ? Color.FromRgb(0x15, 0x16, 0x19) : Color.FromRgb(0xF7, 0xF7, 0xF8);
        var panel = dark ? Color.FromRgb(0x1E, 0x1F, 0x23) : Colors.White;
        var line = dark ? Color.FromRgb(0x2A, 0x2B, 0x30) : Color.FromRgb(0xE4, 0xE5, 0xE8);
        var text = dark ? Color.FromRgb(0xD8, 0xDA, 0xDE) : Color.FromRgb(0x1F, 0x23, 0x28);
        var dim = dark ? Color.FromRgb(0x7C, 0x81, 0x89) : Color.FromRgb(0x8A, 0x90, 0x98);
        var ff = new FontFamily("Segoe UI");
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            void Text(string s, double x, double y, double size, Color c, bool bold = false) =>
                dc.DrawText(new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(ff, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                    size, new SolidColorBrush(c), 1.0), new Point(x, y));
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(panel), new Pen(new SolidColorBrush(line), 1), new Rect(0, 0, w, 52));
            double side = Math.Min(220, w / 5.0);
            dc.DrawRectangle(new SolidColorBrush(panel), new Pen(new SolidColorBrush(line), 1), new Rect(0, 52, side, h - 52));
            Text("Acme Analytics", 20, 15, 16, text, true);
            if (w >= 900) { Text("Search…", w - 520, 17, 13, dim); Text("billing@acme.example", w - 220, 17, 13, text); }
            string[] nav = { "Overview", "Customers", "Revenue", "Reports", "Settings" };
            for (int i = 0; i < nav.Length && 80 + i * 34 < h; i++) Text(nav[i], 24, 76 + i * 34, 13, i == 0 ? text : dim, i == 0);
            double x0 = side + 24;
            Text("Overview", x0, 72, 22, text, true);
            double cw = (w - x0 - 24 - 32) / 3;
            string[] k = { "Revenue", "Active users", "Churn" }, v = { "$48,210", "3,942", "4.8%" };
            for (int i = 0; i < 3; i++)
            {
                var r = new Rect(x0 + i * (cw + 16), 116, cw, 96);
                dc.DrawRoundedRectangle(new SolidColorBrush(panel), new Pen(new SolidColorBrush(line), 1), r, 8, 8);
                Text(k[i], r.X + 16, r.Y + 14, 12, dim);
                Text(v[i], r.X + 16, r.Y + 38, 26, text, true);
            }
            var chart = new Rect(x0, 232, w - x0 - 24, Math.Max(60, h - 256));
            dc.DrawRoundedRectangle(new SolidColorBrush(panel), new Pen(new SolidColorBrush(line), 1), chart, 8, 8);
            Text("Monthly recurring revenue", chart.X + 16, chart.Y + 12, 13, text, true);
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), 2.5) { LineJoin = PenLineJoin.Round };
            var rnd = new Random(7);
            var pts = new List<Point>();
            for (int i = 0; i <= 24; i++)
            {
                double up = 0.3 + 0.5 * i / 24.0 + rnd.NextDouble() * 0.15 - (i is 14 or 15 ? 0.22 : 0);
                pts.Add(new Point(chart.X + 20 + i * (chart.Width - 40) / 24, chart.Bottom - 30 - up * (chart.Height - 60)));
            }
            int shown = Math.Max(1, (int)Math.Round(progress * 24));
            for (int i = 1; i <= shown; i++) dc.DrawLine(pen, pts[i - 1], pts[i]);
            if (cursor is { } c)
            {
                var arrow = Geometry.Parse($"M{c.X},{c.Y} l0,17 l4.5,-4.2 l3.2,7 l2.6,-1.2 l-3.1,-6.8 l6.2,-0.2 Z");
                dc.DrawGeometry(Brushes.Black, new Pen(Brushes.White, 1.2), arrow);
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
'@ | Set-Content (Join-Path $harness 'Program.cs')

& dotnet build (Join-Path $harness 'UiShots.csproj') -c Release "-p:WsDll=$dll" -o (Join-Path $scratch 'h') -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw 'Harness build failed.' }
$exe = Join-Path $scratch 'h\UiShots.exe'

# Sample recording: 12 s at 30 fps with a keyframe every second, like a real WinSnipper recording
# (sparse keyframes make the filmstrip decode from frame 0 for every tile).
& $exe frames (Join-Path $scratch 'frames')
if ($LASTEXITCODE -ne 0) { throw 'Frame rendering failed.' }
$video = Join-Path $scratch 'Recording 2026-09-11 14-40-12.mp4'
& ffmpeg -hide_banner -loglevel error -y -framerate 10 -i (Join-Path $scratch 'frames\f%03d.png') -c:v libx264 -r 30 -g 30 -pix_fmt yuv420p $video
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed.' }

& $exe render $Out $video
if ($LASTEXITCODE -ne 0) { throw 'Rendering failed; see the exception above.' }

if ($Readme) {
    $img = Join-Path $root 'docs\images'
    New-Item -ItemType Directory -Force $img | Out-Null
    Copy-Item (Join-Path $Out 'readme-editor.png') (Join-Path $img 'editor.png') -Force
    Copy-Item (Join-Path $Out 'readme-trim.png') (Join-Path $img 'trim.png') -Force
    Write-Host "README images refreshed in docs\images"
}
Write-Host "Renders in $Out (harness and build in $scratch)"
