using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinSnipper;

/// <summary>Orchestrates one snip: capture → overlay selection → save + clipboard → floating thumbnail.</summary>
public sealed class SnipManager
{
    private bool _active;

    public void StartSnip()
    {
        if (_active) return;
        _active = true;
        try
        {
            // Hide existing thumbnails so they are not baked into the new screenshot.
            FloatingThumb.SetAllVisible(false);
            BitmapSource shot;
            Int32Rect bounds;
            try
            {
                (shot, bounds) = ScreenCapture.CaptureVirtualScreen();
            }
            catch
            {
                FloatingThumb.SetAllVisible(true);
                return;
            }

            var overlay = new SnipOverlay(shot, bounds);
            bool? ok = overlay.ShowDialog();
            FloatingThumb.SetAllVisible(true);

            if (ok == true && overlay.SelectionPx is { Width: > 0, Height: > 0 } sel)
            {
                var cropped = new CroppedBitmap(shot, sel);
                cropped.Freeze();

                string path = NextSnipPath();
                Util.SavePng(cropped, path);
                if (Settings.Current.CopyToClipboard)
                    Util.TrySetClipboard(cropped);

                new FloatingThumb(path, cropped).ShowStacked();
            }
        }
        finally
        {
            _active = false;
        }
    }

    private static string NextSnipPath()
    {
        string baseName = $"Snip {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        string path = Path.Combine(Util.SnipsDir, baseName + ".png");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(Util.SnipsDir, $"{baseName} ({i}).png");
        return path;
    }
}
