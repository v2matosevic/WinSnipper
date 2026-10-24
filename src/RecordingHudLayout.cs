using System.Windows;

namespace WinSnipper;

/// <summary>Recording controls are placed on one real monitor, never in a virtual-desktop gap.</summary>
internal static class RecordingHudLayout
{
    internal static Int32Rect Place(Int32Rect region, Int32Rect work, int width, int height)
    {
        width = Math.Min(Math.Max(1, width), work.Width);
        height = Math.Min(Math.Max(1, height), work.Height);
        int x = region.X + (region.Width - width) / 2;
        var candidates = new[]
        {
            new Int32Rect(x, region.Y + region.Height + 12, width, height),
            new Int32Rect(x, region.Y - height - 12, width, height),
            new Int32Rect(region.X - width - 12, region.Y, width, height),
            new Int32Rect(region.X + region.Width + 12, region.Y, width, height),
        };
        foreach (var candidate in candidates)
        {
            var clamped = Clamp(candidate, work);
            if (!Intersects(clamped, region)) return clamped;
        }
        // Fullscreen: reachable controls take priority. Backend-aware affinity
        // excludes them on WGC; compatibility capture can include the controls.
        return Clamp(new Int32Rect(x, Math.Max(work.Y, region.Y) + 16, width, height), work);
    }

    internal static Int32Rect Clamp(Int32Rect rect, Int32Rect work)
    {
        int w = Math.Min(rect.Width, work.Width), h = Math.Min(rect.Height, work.Height);
        int marginX = Math.Min(8, Math.Max(0, (work.Width - w) / 2));
        int marginY = Math.Min(8, Math.Max(0, (work.Height - h) / 2));
        return new Int32Rect(
            Math.Clamp(rect.X, work.X + marginX, work.X + work.Width - w - marginX),
            Math.Clamp(rect.Y, work.Y + marginY, work.Y + work.Height - h - marginY), w, h);
    }

    internal static bool Intersects(Int32Rect a, Int32Rect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
