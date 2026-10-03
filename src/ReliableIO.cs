using System.IO;

namespace WinSnipper;

internal static class ReliableIO
{
    public static void Write(string path, Action<Stream> write, bool overwrite = true)
    {
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            // Only our uniquely named scratch file is eligible for cleanup.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<bool> RetryAsync(Action action, int attempts = 4, int delayMs = 60, Func<bool>? mayContinue = null)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (mayContinue is not null && !mayContinue()) return false;
            try { action(); return true; }
            catch (Exception) when (attempt + 1 < attempts) { await Task.Delay(delayMs); }
            catch (Exception) { return false; }
        }
        return false;
    }
}
