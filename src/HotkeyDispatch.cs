using System.Collections.Concurrent;

namespace WinSnipper;

/// <summary>A bounded handoff that never executes subscribers inside a native hook.</summary>
internal sealed class HotkeyDispatch(Action<uint, uint, bool> deliver) : IDisposable
{
    private readonly BlockingCollection<(uint Time, uint Receipt, bool Recording)> _requests = new(2);
    private int _snipQueued, _recordQueued;
    private Thread? _thread;

    internal void Start()
    {
        _thread = new Thread(Run)
        { IsBackground = true, Name = "WinSnipper.HotkeyDispatch", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    internal void Enqueue(uint time, bool recording)
    {
        ref int queued = ref (recording ? ref _recordQueued : ref _snipQueued);
        if (Interlocked.Exchange(ref queued, 1) != 0) return;
        try
        {
            if (!_requests.TryAdd((time, unchecked((uint)Environment.TickCount), recording)))
                Interlocked.Exchange(ref queued, 0);
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref queued, 0); }
    }

    private void Run()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            if (request.Recording) Interlocked.Exchange(ref _recordQueued, 0);
            else Interlocked.Exchange(ref _snipQueued, 0);
            try { deliver(request.Time, request.Receipt, request.Recording); }
            catch (Exception ex) { Util.LogCrash("Hotkey dispatch", ex); }
        }
    }

    public void Dispose() => _requests.CompleteAdding();
}
