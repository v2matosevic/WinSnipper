using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinSnipper;

public static class Util
{
    public static string SnipsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinSnipper");

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
