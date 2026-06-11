using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinSnipper;

public static class Util
{
    public static string SnipsDir => Settings.Current.SaveDir;

    public static string FormatHotkey(bool win, bool ctrl, bool alt, bool shift, uint vk)
    {
        var parts = new List<string>(5);
        if (win) parts.Add("Win");
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk).ToString());
        return string.Join(" + ", parts);
    }

    public static string CurrentHotkeyDisplay
    {
        get
        {
            var s = Settings.Current;
            return FormatHotkey(s.ModWin, s.ModCtrl, s.ModAlt, s.ModShift, s.HotkeyVk);
        }
    }

    public static void SavePng(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>Clipboard occasionally throws CLIPBRD_E_CANT_OPEN when another app holds it; retry briefly.</summary>
    public static void TrySetClipboard(BitmapSource image)
    {
        for (int i = 0; i < 4; i++)
        {
            try
            {
                Clipboard.SetImage(image);
                return;
            }
            catch
            {
                Thread.Sleep(60);
            }
        }
    }

    public static void TrySetClipboardText(string text)
    {
        for (int i = 0; i < 4; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch
            {
                Thread.Sleep(60);
            }
        }
    }

    /// <summary>
    /// Runs Windows' built-in OCR over the image and returns the recognized
    /// text (lines joined with newlines), or null if no OCR language is available.
    /// </summary>
    public static async Task<string?> OcrAsync(BitmapSource image)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
                .Select(Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage)
                .FirstOrDefault(x => x is not null);
        if (engine is null) return null;

        // OCR rejects images beyond its max dimension — downscale to fit.
        double maxDim = Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        BitmapSource src = image;
        if (src.PixelWidth > maxDim || src.PixelHeight > maxDim)
        {
            double scale = Math.Min(maxDim / src.PixelWidth, maxDim / src.PixelHeight);
            src = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
        }

        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        encoder.Save(ms);
        ms.Position = 0;

        using var ras = ms.AsRandomAccessStream();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        using var soft = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        var result = await engine.RecognizeAsync(soft);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }
}
