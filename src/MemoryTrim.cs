using System.Diagnostics;
using System.Runtime;

namespace WinSnipper;

/// <summary>
/// Gives memory back after a capture has been dealt with. Every snip and
/// editor session leaves image-sized buffers behind (native bitmaps waiting
/// for finalizers, large-object-heap arrays from PNG encoding), and an idle
/// tray app allocates too little afterwards to make the collector reclaim
/// them: real processes climbed from 350 MB to over 1 GB in steps and stayed
/// there, and Windows then paged that out and back in around every hotkey.
/// A full compacting collection a few seconds after the last capture window
/// closes, while the process is still warm, is the cheap moment to do it.
/// It never runs while a capture, editor or recording is in progress.
/// </summary>
internal static class MemoryTrim
{
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static Func<bool>? _busy;
    private static int _running;

    internal static TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Where the one-line result goes (session.log; tests capture it).</summary>
    internal static Action<string> Log { get; set; } = Util.LogSession;

    /// <param name="busy">True while trimming must wait (called on a pool thread).</param>
    internal static void Initialize(Func<bool> busy) => _busy = busy;

    /// <summary>Call when a capture window closes. Restarts the countdown.</summary>
    internal static void Schedule()
    {
        if (_busy is null) return;
        lock (Gate)
        {
            _timer ??= new Timer(_ => Run());
            _timer.Change(Delay, Timeout.InfiniteTimeSpan);
        }
    }

    private static void Run()
    {
        if (_busy is null || Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            if (_busy()) { Schedule(); return; }
            long before = PrivateBytes();
            var clock = Stopwatch.StartNew();
            Collect();
            Log($"memory-trim private={before >> 20}MB->{PrivateBytes() >> 20}MB took={clock.ElapsedMilliseconds}ms");
        }
        catch (Exception ex) { Util.LogCrash("MemoryTrim", ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    /// <summary>Two passes: the first queues finalizers that own native bitmaps, the second frees what they released.</summary>
    internal static void Collect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static long PrivateBytes()
    {
        using var self = Process.GetCurrentProcess();
        return self.PrivateMemorySize64;
    }
}
