using System;
using System.Threading;

namespace AutoRoute.PipeWire.Process;

/// <summary>
/// Coalesces a burst of triggers into a single callback fired once the triggers go quiet
/// for <c>interval</c>. Used to turn pw-mon's flood of added/changed/removed lines into one
/// reload. An optional <c>maxWait</c> bounds the quiet-period wait: without it, triggers
/// arriving faster than <c>interval</c> re-arm the timer forever and the callback starves;
/// with it, the callback is guaranteed within <c>maxWait</c> of the first un-fired trigger,
/// which also caps how often a continuous flood can fire (once per <c>maxWait</c>).
/// Thread-safe; the callback runs on a threadpool timer thread.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan? _maxWait;
    private readonly Action _callback;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private long? _burstStart; // timestamp of the first trigger since the last fire
    private bool _disposed;

    public Debouncer(TimeSpan interval, Action callback, TimeSpan? maxWait = null, TimeProvider? time = null)
    {
        _interval = interval;
        _maxWait = maxWait;
        _callback = callback;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Register activity; (re)arms the quiet-period timer, clamped by <c>maxWait</c>.</summary>
    public void Trigger()
    {
        lock (_gate)
        {
            if (_disposed) return;

            var delay = _interval;
            if (_maxWait is { } maxWait)
            {
                var now = _time.GetTimestamp();
                _burstStart ??= now;
                var remaining = maxWait - _time.GetElapsedTime(_burstStart.Value, now);
                if (remaining < delay) delay = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _burstStart = null;
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
