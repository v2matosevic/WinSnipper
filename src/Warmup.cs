using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSnipper;

/// <summary>
/// The first capture of a session pays for JIT, BAML parsing and the WPF
/// imaging pipeline all at once — around 800 ms of it on a machine that is
/// otherwise idle. None of that work depends on what is being captured, so it
/// happens once at startup instead, while nobody is waiting. Nothing is put on
/// screen and nothing is written to disk.
/// </summary>
public static class Warmup
{
    /// <summary>Startup path: a warm-up failure must never be the reason the app is not there.</summary>
    public static void Run()
    {
        try { Prime(); }
        catch (Exception ex) { Util.LogCrash("Warmup", ex); }
    }

    /// <summary>The same work, letting failures through so --selftest can see them.</summary>
    public static void Prime()
    {
        var (shot, bounds) = ScreenCapture.CaptureVirtualScreen();
        SnipOverlay.Warm(shot, bounds);
        WarmImagePipeline(shot);
    }

    /// <summary>Crop, PNG encode and offscreen render — the three things every snip ends in.</summary>
    private static void WarmImagePipeline(BitmapSource shot)
    {
        int side = Math.Min(64, Math.Min(shot.PixelWidth, shot.PixelHeight));
        if (side < 1) return;

        var crop = new CroppedBitmap(shot, new Int32Rect(0, 0, side, side));
        crop.Freeze();

        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));
        encoder.Save(ms);

        var visual = new Border
        {
            Width = side,
            Height = side,
            Background = Brushes.Black,
            Child = new Image { Source = crop },
        };
        visual.Measure(new Size(side, side));
        visual.Arrange(new Rect(0, 0, side, side));

        var target = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
    }
}
