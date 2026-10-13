using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinSnipper;

/// <summary>One capture at a time, off both WPF and the shared thread pool.</summary>
internal sealed class CaptureWorker
{
    private readonly BlockingCollection<(TaskCompletionSource<(BitmapSource, Int32Rect)> Completion, PerformanceTrace? Trace)> _requests = new(1);
    private readonly Func<(BitmapSource, Int32Rect)> _capture;

    internal CaptureWorker(Func<(BitmapSource, Int32Rect)> capture)
    {
        _capture = capture;
        var thread = new Thread(Run) { IsBackground = true, Name = "WinSnipper.Capture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static readonly Lazy<CaptureWorker> Shared = new(() => new(ScreenCapture.CaptureVirtualScreen));
    internal static Task<(BitmapSource, Int32Rect)> CaptureAsync(PerformanceTrace? trace = null) => Shared.Value.Enqueue(trace);
    internal static void Shutdown()
    {
        if (Shared.IsValueCreated) Shared.Value.Complete();
    }

    internal Task<(BitmapSource, Int32Rect)> Enqueue(PerformanceTrace? trace = null)
    {
        var completion = new TaskCompletionSource<(BitmapSource, Int32Rect)>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!_requests.TryAdd((completion, trace)))
                completion.SetException(new InvalidOperationException("A screenshot is already queued."));
        }
        catch (InvalidOperationException ex) { completion.SetException(ex); }
        return completion.Task;
    }

    private void Run()
    {
        while (ProcessNext()) { }
    }

    // A consuming iterator retains its Current while waiting for the next item.
    // Keep the completed TCS (and its bitmap) off the idle worker's stack instead.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ProcessNext()
    {
        (TaskCompletionSource<(BitmapSource, Int32Rect)> Completion, PerformanceTrace? Trace) request;
        try { request = _requests.Take(); }
        catch (InvalidOperationException) { return false; } // drained after CompleteAdding
        try
        {
            request.Trace?.Mark("capture-started");
            var result = _capture();
            request.Trace?.Mark("capture-ready");
            request.Completion.SetResult(result);
        }
        catch (Exception ex) { request.Completion.SetException(ex); }
        return true;
    }

    // Complete a private test worker, or drain at normal application shutdown.
    internal void Complete() => _requests.CompleteAdding();
}
