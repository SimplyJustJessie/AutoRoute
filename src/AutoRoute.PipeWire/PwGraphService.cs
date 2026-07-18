using System;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire;

/// <inheritdoc cref="IPwGraphService"/>
public sealed class PwGraphService : IPwGraphService, IAsyncDisposable
{
    private readonly PwDumpReader _reader;
    private readonly IGraphMonitor _monitor;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private volatile PwGraph _current = PwGraph.Empty;
    private int _started;

    // Coalescing state for monitor-triggered reloads: signals that arrive while a pw-dump is in
    // flight collapse into ONE follow-up reload instead of queueing a dump per signal.
    private readonly object _refreshSignal = new();
    private bool _refreshRunning;
    private bool _refreshPending;

    public PwGraphService(PwDumpReader reader, IGraphMonitor monitor, ILogger<PwGraphService>? log = null)
    {
        _reader = reader;
        _monitor = monitor;
        _log = log ?? NullLogger<PwGraphService>.Instance;
    }

    public PwGraph Current => _current;

    public event EventHandler<PwGraph>? GraphUpdated;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        // Prime the snapshot before the monitor starts pushing signals.
        try { await RefreshAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "initial graph load failed; starting empty"); }

        _monitor.Changed += OnMonitorChanged;
        await _monitor.StartAsync(ct).ConfigureAwait(false);
    }

    private async void OnMonitorChanged(object? sender, EventArgs e)
    {
        // Drain pattern: the first signal starts a reload loop; signals landing mid-reload just set
        // the pending flag. However many signals arrive during one pw-dump, exactly one more runs —
        // reloads can no longer pile up behind a slow dump (each queued waiter used to run its own).
        lock (_refreshSignal)
        {
            if (_refreshRunning)
            {
                _refreshPending = true;
                return;
            }
            _refreshRunning = true;
        }

        while (true)
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "reload after change signal failed"); }

            lock (_refreshSignal)
            {
                if (!_refreshPending)
                {
                    _refreshRunning = false;
                    return;
                }
                _refreshPending = false;
            }
        }
    }

    public async Task<PwGraph> RefreshAsync(CancellationToken ct = default)
    {
        await _reloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var graph = await _reader.LoadAsync(ct).ConfigureAwait(false);
            _current = graph;
            GraphUpdated?.Invoke(this, graph);
            return graph;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task StopAsync()
    {
        _monitor.Changed -= OnMonitorChanged;
        await _monitor.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _monitor.DisposeAsync().ConfigureAwait(false);
        _reloadGate.Dispose();
    }
}
