using System;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Design;

/// <summary>
/// In-memory <see cref="IPwGraphService"/> for standalone UI development: serves a captured graph
/// as the snapshot and can raise <see cref="GraphUpdated"/> on demand to exercise the live diff-merge.
/// Wave 3 replaces this with the real <c>PwGraphService</c> (pw-dump + pw-mon) — same interface.
/// </summary>
public sealed class MockPwGraphService : IPwGraphService
{
    private PwGraph _current;

    public MockPwGraphService(PwGraph? initial = null) => _current = initial ?? PwGraph.Empty;

    public PwGraph Current => _current;

    public event EventHandler<PwGraph>? GraphUpdated;

    public Task StartAsync(CancellationToken ct = default)
    {
        // Publish the initial snapshot exactly like the real service does after its first load.
        GraphUpdated?.Invoke(this, _current);
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public Task<PwGraph> RefreshAsync(CancellationToken ct = default) => Task.FromResult(_current);

    /// <summary>Dev hook: swap the snapshot and fire <see cref="GraphUpdated"/> (simulates a stream start/stop).</summary>
    public void Publish(PwGraph graph)
    {
        _current = graph;
        GraphUpdated?.Invoke(this, graph);
    }
}
