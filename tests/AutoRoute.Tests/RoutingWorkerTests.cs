using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.App.Hosting;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.Tests;

/// <summary>
/// Exercises the always-on reconcile loop with fakes (no real PipeWire): a graph update or a rule
/// change triggers <see cref="IReconciler.ReconcileAsync"/>, the "Automation Enabled" gate suppresses
/// reconciles while off and catches up when flipped back on.
/// </summary>
public sealed class RoutingWorkerTests
{
    [Fact]
    public async Task Reconciles_on_graph_and_rule_triggers_and_honours_the_automation_gate()
    {
        var graph = new FakeGraphService();
        var store = new FakeRuleStore();
        var reconciler = new CountingReconciler();
        var worker = new RoutingWorker(graph, store, reconciler, NullLogger<RoutingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // ExecuteAsync subscribes, then starts the graph service whose initial snapshot
            // (FakeGraphService.StartAsync raises GraphUpdated, like the real PwGraphService) drives
            // the first reconcile — synchronizing the test with the worker having wired up.
            await WaitUntil(() => reconciler.Count >= 1);

            // A further graph update triggers another reconcile.
            var beforeUpdate = reconciler.Count;
            graph.RaiseUpdated();
            await WaitUntil(() => reconciler.Count > beforeUpdate);

            // Automation OFF → further updates are ignored.
            worker.AutomationEnabled = false;
            var whileOff = reconciler.Count;
            graph.RaiseUpdated();
            await Task.Delay(350);
            Assert.Equal(whileOff, reconciler.Count);

            // Automation ON → schedules a catch-up reconcile.
            worker.AutomationEnabled = true;
            await WaitUntil(() => reconciler.Count > whileOff);

            // A rule change also triggers a reconcile.
            var beforeRuleChange = reconciler.Count;
            store.RaiseChanged();
            await WaitUntil(() => reconciler.Count > beforeRuleChange);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(25);
        Assert.True(condition(), "condition not met within timeout");
    }

    private sealed class FakeGraphService : IPwGraphService
    {
        public PwGraph Current { get; } = PwGraph.Empty;
        public event EventHandler<PwGraph>? GraphUpdated;
        public Task StartAsync(CancellationToken ct = default)
        {
            // Mirror PwGraphService: the initial load fires GraphUpdated once the monitor is live.
            GraphUpdated?.Invoke(this, Current);
            return Task.CompletedTask;
        }
        public Task StopAsync() => Task.CompletedTask;
        public Task<PwGraph> RefreshAsync(CancellationToken ct = default)
        {
            GraphUpdated?.Invoke(this, Current);
            return Task.FromResult(Current);
        }
        public void RaiseUpdated() => GraphUpdated?.Invoke(this, Current);
    }

    private sealed class FakeRuleStore : IRuleStore
    {
        public RulesDocument Current { get; private set; } = RulesDocument.Empty;
        public event EventHandler<RulesDocument>? Changed;
        public Task<RulesDocument> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveAsync(RulesDocument document, CancellationToken ct = default)
        {
            Current = document;
            Changed?.Invoke(this, document);
            return Task.CompletedTask;
        }
        public void RaiseChanged() => Changed?.Invoke(this, Current);
    }

    private sealed class CountingReconciler : IReconciler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task ReconcileAsync(PwGraph graph, RulesDocument rules, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }
}
