using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinSnipper;

/// <summary>Measures input-to-first-render without writing logs on the UI/hook thread.</summary>
public sealed class PerformanceTrace(string operation, uint? inputTimestamp = null, uint? hookReceipt = null)
{
    private static readonly object LogGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    // KBDLLHOOKSTRUCT.time and TickCount share the Windows uptime clock.
    // Unsigned subtraction also handles the 32-bit clock wrapping.
    private readonly List<string> _steps = inputTimestamp is { } timestamp
        ? new() { $"input-age={unchecked((uint)Environment.TickCount - timestamp)}ms" }
        : new();
    private bool _finished;
    private Action? _detachWindow;
    private readonly int[] _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private readonly TimeSpan _pauseStart = GC.GetTotalPauseDuration();
    private readonly object _gate = new();
    private readonly uint _handoffAge = hookReceipt is { } receipt
        ? unchecked((uint)Environment.TickCount - receipt) : 0;

    public void Mark(string stage)
    {
        lock (_gate) _steps.Add($"{stage}={_clock.Elapsed.TotalMilliseconds:F1}ms");
    }

    public void TrackWindow(Window window)
    {
        Mark("constructed");
        DispatcherOperation? renderFence = null;
        bool reused = window is SnipOverlay { IsReusable: true };
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Mark("hwnd-ready");
        void Initialized(object? sender, EventArgs args) => Mark("hwnd-ready");
        void Loaded(object sender, RoutedEventArgs args) => Mark("loaded");
        void Rendered(object? sender, EventArgs args) => Finish("rendered");
        void Closed(object? sender, EventArgs args) => Finish("closed-before-render");
        void SceneRendering(object? sender, EventArgs args)
        {
            if (!window.IsVisible) return;
            CompositionTarget.Rendering -= SceneRendering;
            Mark("scene-ready");
            // ContentRendered follows changes to Window.Content, not every
            // Show of a retained window. Use the same post-render Input fence
            // for each screenshot show. Neither marker proves DWM presentation.
            renderFence = window.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                (Action)(() => Finish("rendered")));
        }
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!window.IsVisible) Finish("hidden-before-render");
        }
        window.SourceInitialized += Initialized;
        window.Loaded += Loaded;
        window.ContentRendered += Rendered;
        window.Closed += Closed;
        window.IsVisibleChanged += VisibilityChanged;
        if (reused) CompositionTarget.Rendering += SceneRendering;
        _detachWindow = () =>
        {
            window.SourceInitialized -= Initialized;
            window.Loaded -= Loaded;
            window.ContentRendered -= Rendered;
            window.Closed -= Closed;
            window.IsVisibleChanged -= VisibilityChanged;
            if (reused) CompositionTarget.Rendering -= SceneRendering;
            renderFence?.Abort();
        };
    }

    public void Finish(string stage)
    {
        if (_finished) return;
        _finished = true;
        Mark(stage);
        _detachWindow?.Invoke();
        _detachWindow = null; // A retained window must not retain every old trace.
        if (inputTimestamp is { } input && hookReceipt is { } receipt)
        {
            _steps.Add($"hook-age={unchecked(receipt - input)}ms");
            _steps.Add($"handoff={_handoffAge}ms");
        }
        _steps.Add($"render-tier={RenderCapability.Tier >> 16}");
        try
        {
            using var process = Process.GetCurrentProcess();
            _steps.Add($"priority={process.PriorityClass}");
            // Real-use evidence for footprint growth and the pinned working set.
            _steps.Add($"ws={process.WorkingSet64 >> 20}MB private={process.PrivateMemorySize64 >> 20}MB");
        }
        catch { _steps.Add("priority=unavailable"); }
        _steps.Add($"gc={GC.CollectionCount(0) - _collections[0]}/{GC.CollectionCount(1) - _collections[1]}/{GC.CollectionCount(2) - _collections[2]}");
        _steps.Add($"gc-pause={(GC.GetTotalPauseDuration() - _pauseStart).TotalMilliseconds:F1}ms");
        string line = $"performance {operation} {string.Join(' ', _steps)}";
        _ = Task.Run(() => { lock (LogGate) Util.LogSession(line); });
    }
}
