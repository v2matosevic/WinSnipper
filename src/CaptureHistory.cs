using System.IO;
using System.Windows.Media.Imaging;

namespace WinSnipper;

internal sealed record CaptureEntry(string Path, DateTime Modified, long Bytes, bool IsVideo)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Kind => IsVideo ? "Recording" : "Screenshot";
    public string Details => $"{Modified:dd MMM yyyy, HH:mm}  ·  {Size}";
    public string Size => Bytes >= 1_048_576 ? $"{Bytes / 1_048_576d:0.#} MB" : $"{Math.Max(1, Bytes / 1024):0} KB";
}

internal sealed record HistoryScan(List<CaptureEntry> Entries, string? Error);

internal static class CaptureHistory
{
    public static HistoryScan Scan(string saveDir)
    {
        var entries = new List<CaptureEntry>();
        string? error = null;
        foreach (string dir in new[] { saveDir, System.IO.Path.Combine(saveDir, "Recordings") })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string path in Directory.EnumerateFiles(dir))
                {
                    string extension = System.IO.Path.GetExtension(path);
                    bool video = extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
                    if ((!video && !extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) ||
                        System.IO.Path.GetFileName(path).StartsWith("_selftest", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var file = new FileInfo(path);
                        entries.Add(new CaptureEntry(file.FullName, file.LastWriteTime, file.Length, video));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { error = "Some captures could not be read. Check the save folder and refresh."; }
            catch (UnauthorizedAccessException) { error = "The save folder cannot be read. Check its permissions and refresh."; }
        }
        return new HistoryScan(entries.OrderByDescending(e => e.Modified)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList(), error);
    }

    public static BitmapSource LoadImage(string path, int previewWidth = 0)
    {
        // OnLoad releases the file before an editor, cleanup or another capture needs it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (previewWidth > 0)
        {
            var header = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            double scale = Math.Min(1, previewWidth / (double)Math.Max(header.PixelWidth, header.PixelHeight));
            image.DecodePixelWidth = Math.Max(1, (int)Math.Round(header.PixelWidth * scale));
            stream.Position = 0;
        }
        image.EndInit();
        image.Freeze();
        return image;
    }
}
