using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinSnipper;

/// <summary>The session's screenshot worker and the UI half of each capture.</summary>
internal static class CaptureWorker
{
    private static readonly Lazy<CaptureWorker<ScreenCapture.SharedFrame>> Shared = new(() => new(ScreenCapture.BlitShared));

    /// <summary>
    /// Blits on the worker, then presents the session bitmap on the caller's
    /// (UI) thread. The image is the selector's live view of the shared
    /// section: show it, then take <see cref="ScreenCapture.CopyRegion"/>.
    /// </summary>
    internal static async Task<(BitmapSource, Int32Rect)> CaptureAsync(PerformanceTrace? trace = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = await Shared.Value.Enqueue(trace);
        var image = dispatcher.CheckAccess()
            ? ScreenCapture.PresentShared(frame)
            : await dispatcher.InvokeAsync(() => ScreenCapture.PresentShared(frame));
        return (image, frame.Bounds);
    }

    internal static void Shutdown()
    {
        if (Shared.IsValueCreated) Shared.Value.Complete();
    }
}

/// <summary>One capture at a time, off both WPF and the shared thread pool.</summary>
internal sealed class CaptureWorker<T>
{
    private readonly BlockingCollection<(TaskCompletionSource<T> Completion, PerformanceTrace? Trace)> _requests = new(1);
    private readonly Func<T> _capture;

    internal CaptureWorker(Func<T> capture)
    {
        _capture = capture;
        var thread = new Thread(Run) { IsBackground = true, Name = "WinSnipper.Capture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal Task<T> Enqueue(PerformanceTrace? trace = null)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
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
    // Keep the completed TCS (and its result) off the idle worker's stack instead.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ProcessNext()
    {
        (TaskCompletionSource<T> Completion, PerformanceTrace? Trace) request;
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
