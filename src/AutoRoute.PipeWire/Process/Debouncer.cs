using System;
using System.Threading;

namespace AutoRoute.PipeWire.Process;

/// <summary>
/// Coalesces a burst of triggers into a single callback fired once the triggers go quiet
/// for <c>interval</c>. Used to turn pw-mon's flood of added/changed/removed lines into one
/// reload. Thread-safe; the callback runs on a threadpool timer thread.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Action _callback;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;

    public Debouncer(TimeSpan interval, Action callback)
    {
        _interval = interval;
        _callback = callback;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Register activity; (re)arms the quiet-period timer.</summary>
    public void Trigger()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        _callback();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
    }
}
