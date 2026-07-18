using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire;

/// <summary>
/// Fallback <see cref="IGraphMonitor"/> (the <c>--poll</c> mode): periodically runs
/// <c>pw-dump</c> and raises <see cref="Changed"/> only when the graph's structural signature
/// (node ids, port ids, and each link's endpoints + managed tag) differs from the previous
/// poll. Used where pw-mon is unavailable/flaky. Slower to react than <see cref="PwMonMonitor"/>
/// but has no long-lived child process.
/// </summary>
public sealed class PollingGraphMonitor : IGraphMonitor
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly PwDumpReader _reader;
    private readonly TimeSpan _interval;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private string? _lastSignature;

    public event EventHandler? Changed;

    public PollingGraphMonitor(PwDumpReader reader, TimeSpan? interval = null, ILogger<PollingGraphMonitor>? log = null)
    {
        _reader = reader;
        _interval = interval ?? DefaultInterval;
        _log = log ?? NullLogger<PollingGraphMonitor>.Instance;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var graph = await _reader.LoadAsync(ct).ConfigureAwait(false);
                var sig = Signature(graph);
                if (sig != _lastSignature)
                {
                    _lastSignature = sig;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "poll cycle failed; will retry");
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static string Signature(PwGraph g)
    {
        var nodes = string.Join(",", g.NodesById.Keys.OrderBy(x => x));
        var ports = string.Join(",", g.PortsById.Keys.OrderBy(x => x));
        var links = string.Join(",", g.Links
            .OrderBy(l => l.Id)
            .Select(l => $"{l.Id}:{l.OutPortId}->{l.InPortId}:{(l.IsManaged ? "m" : "u")}"));
        return $"N[{nodes}]P[{ports}]L[{links}]";
    }

    public async Task StopAsync()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
