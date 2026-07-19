using System;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire;

/// <summary>
/// Default <see cref="IGraphMonitor"/>: spawns <c>pw-mon</c> and treats Node/Port/Link records
/// (classified by <see cref="PwMonLineFilter"/> — Client/Device/Metadata churn is ignored) as
/// activity, coalesced through a ~250 ms debounce (bounded by a max-wait so a continuous flood
/// still signals about once a second instead of starving) into a single <see cref="Changed"/>
/// signal. If pw-mon dies, it auto-respawns with exponential backoff and forces one
/// <see cref="Changed"/> so the consumer does a full reload (nothing is missed across the gap).
/// pw-mon output is only ever classified as trigger/no-trigger, never parsed into a graph.
///
/// pw-mon runs with <c>--hide-props --hide-params</c>: with them the same event stream shrinks
/// ~40× (measured: 1.07 MB → 26 KB per 10 s on a desktop with active audio). The full output was
/// the app's single largest CPU cost — every line-buffered write crossed epoll, a threadpool
/// dispatch and a string allocation before the filter could even look at it, burning ~35% of a
/// core around the clock. The headers, id and type lines the filter needs all survive.
/// </summary>
public sealed class PwMonMonitor : IGraphMonitor
{
    public const string Tool = "pw-mon";

    // -N: never emit ANSI colors, so the filter's prefix matching can't meet an escape code.
    private static readonly string[] ToolArgs = { "--hide-props", "--hide-params", "--no-colors" };

    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly ILogger _log;
    private readonly Debouncer _debounce;
    private readonly PwMonLineFilter _filter = new();
    private readonly CancellationTokenSource _cts = new();
    private LongRunningProcess? _proc;
    private Task? _supervisor;
    private TimeSpan _backoff = MinBackoff;

    public event EventHandler? Changed;

    public PwMonMonitor(ILogger<PwMonMonitor>? log = null, TimeSpan? debounce = null)
    {
        _log = log ?? NullLogger<PwMonMonitor>.Instance;
        _debounce = new Debouncer(debounce ?? DefaultDebounce, RaiseChanged, maxWait: DefaultMaxWait);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _supervisor = Task.Run(() => SuperviseAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var exited = new TaskCompletionSource();
            var proc = new LongRunningProcess(Tool, ToolArgs, _log);
            proc.LineReceived += OnLine;
            proc.Exited += code =>
            {
                _log.LogWarning("{Tool} exited (code {Code}); will respawn after {Backoff}", Tool, code, _backoff);
                exited.TrySetResult();
            };

            try
            {
                _filter.Reset(); // fresh process → fresh record stream
                proc.Start();
                _proc = proc;
                _backoff = MinBackoff; // healthy start resets backoff

                // A fresh pw-mon dumps the whole graph as 'added:' lines; force one reload.
                _debounce.Trigger();

                await exited.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "failed to run {Tool}; falling back to backoff", Tool);
            }
            finally
            {
                proc.LineReceived -= OnLine;
                await proc.DisposeAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) break;

            // Respawn gap: force a reload so nothing that changed while pw-mon was down is missed.
            _debounce.Trigger();
            try { await Task.Delay(_backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            _backoff = TimeSpan.FromMilliseconds(Math.Min(_backoff.TotalMilliseconds * 2, MaxBackoff.TotalMilliseconds));
        }
    }

    private void OnLine(string line)
    {
        if (_filter.ShouldTrigger(line))
            _debounce.Trigger();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public async Task StopAsync()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        if (_supervisor is not null)
        {
            try { await _supervisor.ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (_proc is not null)
            await _proc.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _debounce.Dispose();
        _cts.Dispose();
    }
}
