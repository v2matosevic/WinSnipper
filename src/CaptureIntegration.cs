using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinSnipper;

internal static class CaptureExport
{
    public static Func<byte[]> Snapshot(BitmapSource image)
    {
        var snapshot = image.Clone(); snapshot.Freeze();
        return () => { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(snapshot)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray(); };
    }
}
public partial class EditorWindow
{
    private void AttachCapture_Click(object sender, RoutedEventArgs e)
    {
        var export = CaptureExport.Snapshot(Composite());
        new CaptureHubWindow(Path.GetFileName(_path), export) { Owner = this }.Show();
    }
}
public partial class FloatingThumb
{
    private void AttachCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_isVideo) return;
        _pinned = true; _dismissTimer.Stop();
        new CaptureHubWindow(Path.GetFileName(_path), CaptureExport.Snapshot(_img)) { Owner = this }.Show();
    }
}
public partial class HistoryWindow
{
    private void AttachCapture_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected;
        if (entry is null || entry.IsVideo || _acting) return;
        string path = entry.Path;
        new CaptureHubWindow(entry.Name, () => File.ReadAllBytes(path)) { Owner = this }.Show();
    }
}
