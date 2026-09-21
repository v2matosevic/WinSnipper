using System.Diagnostics;

namespace WinSnipper;

/// <summary>
/// A screenshot tool is only ever wanted at the moment the machine is already
/// busy, and a tray app that has been idle for hours starts that moment at the
/// back of the queue. WinSnipper idles at Normal priority like anything else
/// and lifts itself above the noise for the few hundred milliseconds a capture
/// actually takes.
///
/// The boost is reference-counted and also expires on its own: an overlay left
/// open on an unattended desk must not leave the process sitting at High
/// forever.
/// </summary>
public static class Responsiveness
{
    private static readonly TimeSpan MaxBoost = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static readonly Process Self = Process.GetCurrentProcess();

    private static int _depth;
    private static Timer? _expiry;
    private static int _generation;

    /// <summary>Raises priority now and restores it when the scope is disposed.</summary>
    public static IDisposable Interactive()
    {
        lock (Gate)
        {
            if (_depth++ == 0)
                Apply(ProcessPriorityClass.High);
            _expiry?.Dispose();
            int generation = ++_generation;
            _expiry = new Timer(_ => Expire(generation), null, MaxBoost, Timeout.InfiniteTimeSpan);
            return new Scope(generation);
        }
    }

    private static void Release(int generation)
    {
        lock (Gate)
        {
            // An expired scope must not decrement a newer boost's count.
            if (generation <= _expiredGeneration || _depth == 0) return;
            if (--_depth > 0) return;
            Drop();
        }
    }

    private static int _expiredGeneration;

    private static void Expire(int generation)
    {
        lock (Gate)
        {
            if (generation != _generation) return; // disposed timer already queued
            Drop();
        }
    }

    private static void Drop()
    {
        lock (Gate)
        {
            _depth = 0;
            _expiredGeneration = _generation;
            _expiry?.Dispose();
            _expiry = null;
            Apply(ProcessPriorityClass.Normal);
        }
    }

    private static void Apply(ProcessPriorityClass priority)
    {
        // Group policy or a constrained job object can refuse this; a capture
        // at normal priority is still a capture.
        try { Self.PriorityClass = priority; }
        catch { }
    }

    private sealed class Scope(int generation) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Release(generation);
        }
    }
}
