using System.Reflection;
using System.Windows.Threading;

namespace WinSnipper;

/// <summary>
/// Records when the UI thread stops answering, and what it was doing. A frozen
/// tray app looks the same as a slow one from outside; Windows only reports
/// "not responding". A stall of a second and a half or more is written to
/// session.log with the stage that was running, including while it is still
/// going on, so a hang that ends with the process being closed leaves a line.
/// </summary>
internal static class UiWatchdog
{
    private const int BeatMs = 250, PollMs = 500, StallMs = 1500;

    private static long _beat;
    private static volatile string? _stage;
    private static volatile DispatcherOperation? _operation;
    private static Thread? _thread;
    private static volatile bool _stopped;

    /// <summary>Names the work the UI thread is about to do. Dispose when it is done.</summary>
    internal static Scope Enter(string stage)
    {
        var previous = _stage;
        _stage = stage;
        return new Scope(previous);
    }

    internal readonly struct Scope(string? previous) : IDisposable
    {
        public void Dispose() => _stage = previous;
    }

    /// <summary>Call once on the UI thread.</summary>
    internal static void Start(Dispatcher dispatcher, Action<string>? log = null)
    {
        if (_thread is not null) return;
        log ??= Util.LogSession;
        Interlocked.Exchange(ref _beat, Environment.TickCount64);
        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromMilliseconds(BeatMs) };
        timer.Tick += (_, _) => Interlocked.Exchange(ref _beat, Environment.TickCount64);
        timer.Start();
        dispatcher.Hooks.OperationStarted += (_, e) => _operation = e.Operation;
        dispatcher.Hooks.OperationCompleted += (_, _) => _operation = null;
        dispatcher.Hooks.OperationAborted += (_, _) => _operation = null;
        dispatcher.ShutdownStarted += (_, _) => _stopped = true;

        _thread = new Thread(() => Watch(log)) { IsBackground = true, Name = "WinSnipper.UiWatchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private static void Watch(Action<string> log)
    {
        long lastPoll = Environment.TickCount64;
        long stallStart = 0;   // the heartbeat that preceded the current stall
        int reported = 0;      // seconds of the current stall already logged while it lasts
        string? during = null;
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            long now = Environment.TickCount64;
            long pollGap = now - lastPoll;
            lastPoll = now;

            // This thread was not scheduled either: the whole process (or the
            // machine) stood still, or the PC slept. Not a UI-thread finding.
            if (pollGap > PollMs + StallMs)
            {
                if (pollGap < 120_000) log($"process-stall duration={pollGap}ms");
                stallStart = 0;
                reported = 0;
                continue;
            }

            long beat = Interlocked.Read(ref _beat);
            long age = now - beat;
            if (age >= StallMs)
            {
                if (stallStart != beat)
                {
                    stallStart = beat;
                    reported = 0;
                    during = Describe();
                }
                // A hang may never recover; say so while it is still happening.
                int seconds = (int)(age / 1000);
                if ((seconds >= 5 && reported < 5) || (seconds >= 30 && reported < 30))
                {
                    reported = seconds >= 30 ? 30 : 5;
                    log($"ui-stall ongoing={age}ms {during}");
                }
            }
            else if (stallStart != 0)
            {
                log($"ui-stall duration={beat - stallStart}ms {during}");
                stallStart = 0;
                reported = 0;
            }
        }
    }

    private static string Describe()
    {
        string stage = _stage ?? "none";
        string operation = "none";
        try
        {
            if (_operation is { } op)
            {
                var method = OperationMethod?.GetValue(op) as Delegate;
                if (method is not null)
                    operation = $"{method.Method.DeclaringType?.Name}.{method.Method.Name}";
            }
        }
        catch { operation = "unknown"; }
        return $"stage={stage} operation={operation.Replace(' ', '_')}";
    }

    private static readonly FieldInfo? OperationMethod =
        typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
}
