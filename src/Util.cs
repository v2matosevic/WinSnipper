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

    /// <summary>Appends to %APPDATA%\WinSnipper\crash.log (trimmed at ~1 MB).</summary>
    public static void LogCrash(string source, Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSnipper");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "crash.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.Delete(path);
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // logging must never crash the crash handler
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

    /// <summary>True in the OCR flavor of the build (/p:EnableOcr=true).</summary>
#if OCR
    public static readonly bool OcrSupported = true;
#else
    public static readonly bool OcrSupported = false;
#endif

#if OCR
    /// <summary>
    /// Runs Windows' built-in OCR over the image and returns the recognized
    /// text (lines joined with newlines), or null if no OCR language is available.
    /// </summary>
    public static async Task<string?> OcrAsync(BitmapSource image)
    {
        var engine = CreateOcrEngine();
        if (engine is null) return null;

        // Windows OCR is markedly more accurate when glyphs are large; capture
        // text (terminals, UI) is usually small, so upscale up to 2x as long as
        // we stay inside the engine's max dimension. Oversized images get
        // downscaled to fit instead.
        double maxDim = Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        BitmapSource src = image;
        double largest = Math.Max(src.PixelWidth, src.PixelHeight);
        double scale = Math.Min(2.0, maxDim / largest);
        if (Math.Abs(scale - 1.0) > 0.05)
            src = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));

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

    /// <summary>
    /// Engine preference: Croatian (if its OCR pack is installed) so diacritics
    /// (č ć š ž đ) survive, then the user's profile languages, then en-US,
    /// then anything available.
    /// </summary>
    private static Windows.Media.Ocr.OcrEngine? CreateOcrEngine() =>
        TryLang("hr")
        ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
        ?? TryLang("en-US")
        ?? Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
            .Select(Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage)
            .FirstOrDefault(x => x is not null);

    private static Windows.Media.Ocr.OcrEngine? TryLang(string tag)
    {
        try { return Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag)); }
        catch { return null; }
    }

    /// <summary>Display name of the OCR engine that would be used right now, or null.</summary>
    public static string? OcrEngineLanguage()
    {
        try { return CreateOcrEngine()?.RecognizerLanguage.DisplayName; }
        catch { return null; }
    }

    /// <summary>True if an OCR pack for the user's primary language is available.</summary>
    public static bool UserLanguageOcrInstalled()
    {
        string two = UserLanguageTag().Split('-')[0];
        try
        {
            return Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
                .Any(l => l.LanguageTag.StartsWith(two, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
#else
    // Lite build stubs — UI hides OCR affordances when OcrSupported is false.
    public static Task<string?> OcrAsync(BitmapSource image) => Task.FromResult<string?>(null);
    public static string? OcrEngineLanguage() => null;
    public static bool UserLanguageOcrInstalled() => false;
#endif

    /// <summary>The user's primary language as a specific culture tag (e.g. "hr-HR").</summary>
    public static string UserLanguageTag()
    {
        try
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            if (culture.Name.Contains('-')) return culture.Name;
            return System.Globalization.CultureInfo.CreateSpecificCulture(culture.Name).Name;
        }
        catch
        {
            return "en-US";
        }
    }

    /// <summary>
    /// Launches an elevated one-liner (UAC prompt) that installs the Windows
    /// OCR capability for the given language. The packs are Windows components
    /// and cannot be bundled with the app.
    /// </summary>
    public static void LaunchOcrPackInstall(string tag)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -Command \"Add-WindowsCapability -Online -Name 'Language.OCR~~~{tag}~0.0.1.0'\"",
            Verb = "runas",
            UseShellExecute = true,
        };
        try { System.Diagnostics.Process.Start(psi); }
        catch { /* user declined UAC */ }
    }
}
