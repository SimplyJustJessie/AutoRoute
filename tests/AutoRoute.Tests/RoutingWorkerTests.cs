using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        var worker = new RoutingWorker(
            graph, store, reconciler, new CountingSinkReconciler(), NullLogger<RoutingWorker>.Instance);

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

    [Fact]
    public async Task Sink_reconcile_runs_before_link_reconcile_each_pass()
    {
        var order = new List<string>();
        var graph = new FakeGraphService();
        var store = new FakeRuleStore();
        var reconciler = new CountingReconciler(() => { lock (order) order.Add("links"); });
        var sinkReconciler = new CountingSinkReconciler(() => { lock (order) order.Add("sinks"); });
        var worker = new RoutingWorker(graph, store, reconciler, sinkReconciler, NullLogger<RoutingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => reconciler.Count >= 1);
            lock (order)
            {
                // Every pass is sinks-then-links (ADR-0011: a created sink's node lands in a later
                // snapshot, so ordering within one pass is what keeps creation ahead of routing).
                Assert.Equal(order.Count / 2, order.Where((s, i) => i % 2 == 0 && s == "sinks").Count());
                Assert.Equal("sinks", order[0]);
                Assert.Equal("links", order[1]);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task Sink_reconcile_failure_does_not_block_link_reconcile()
    {
        var graph = new FakeGraphService();
        var store = new FakeRuleStore();
        var reconciler = new CountingReconciler();
        var sinkReconciler = new CountingSinkReconciler(() => throw new InvalidOperationException("pactl exploded"));
        var worker = new RoutingWorker(graph, store, reconciler, sinkReconciler, NullLogger<RoutingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => reconciler.Count >= 1); // links still ran
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
        private readonly Action? _onReconcile;
        private int _count;
        public CountingReconciler(Action? onReconcile = null) => _onReconcile = onReconcile;
        public int Count => Volatile.Read(ref _count);
        public Task ReconcileAsync(PwGraph graph, RulesDocument rules, CancellationToken ct = default)
        {
            _onReconcile?.Invoke();
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingSinkReconciler : ISinkReconciler
    {
        private readonly Action? _onEnsure;
        private int _count;
        public CountingSinkReconciler(Action? onEnsure = null) => _onEnsure = onEnsure;
        public int Count => Volatile.Read(ref _count);
        public Task EnsureAsync(PwGraph graph, RulesDocument rules, CancellationToken ct = default)
        {
            _onEnsure?.Invoke();
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }
}
