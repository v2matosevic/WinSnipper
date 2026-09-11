using System.Diagnostics;
using System.Windows;

namespace WinSnipper;

/// <summary>Measures input-to-first-render without writing logs on the UI/hook thread.</summary>
public sealed class PerformanceTrace(string operation, uint? inputTimestamp = null)
{
    private static readonly object LogGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    // KBDLLHOOKSTRUCT.time and TickCount share the Windows uptime clock.
    // Unsigned subtraction also handles the 32-bit clock wrapping.
    private readonly List<string> _steps = inputTimestamp is { } timestamp
        ? new() { $"input-age={unchecked((uint)Environment.TickCount - timestamp)}ms" }
        : new();
    private bool _finished;

    public void Mark(string stage) => _steps.Add($"{stage}={_clock.Elapsed.TotalMilliseconds:F1}ms");

    public void TrackWindow(Window window)
    {
        Mark("constructed");
        window.ContentRendered += (_, _) => Finish("rendered");
        window.Closed += (_, _) => Finish("closed-before-render");
    }

    public void Finish(string stage)
    {
        if (_finished) return;
        _finished = true;
        Mark(stage);
        string line = $"performance {operation} {string.Join(' ', _steps)}";
        _ = Task.Run(() => { lock (LogGate) Util.LogSession(line); });
    }
}
