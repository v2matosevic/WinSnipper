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
}
