using System;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

/// <summary>
/// The graph service must republish only snapshots that actually changed: pw-mon signals fire for
/// plenty of activity (volume changes, client churn, link state flips) whose dump parses to an
/// identical graph, and each swallowed republish saves a reconcile pass plus a full board rebuild.
/// </summary>
public sealed class PwGraphServiceTests
{
    private sealed class ManualMonitor : IGraphMonitor
    {
        public event EventHandler? Changed;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public async Task Identical_reload_is_swallowed_and_a_real_change_republishes()
    {
        var runner = new FakeProcessRunner();
        var service = new PwGraphService(new PwDumpReader(runner), new ManualMonitor());
        var updates = 0;
        service.GraphUpdated += (_, _) => updates++;

        // Initial load publishes the first snapshot.
        runner.EnqueueStdout(Fixtures.PwDumpSampleJson);
        await service.StartAsync();
        Assert.Equal(1, updates);
        var first = service.Current;

        // Same dump again (param-only churn) → no republish, same snapshot instance kept.
        runner.EnqueueStdout(Fixtures.PwDumpSampleJson);
        var unchanged = await service.RefreshAsync();
        Assert.Equal(1, updates);
        Assert.Same(first, unchanged);
        Assert.Same(first, service.Current);

        // A structurally different dump → republish.
        runner.EnqueueStdout("[]");
        var changed = await service.RefreshAsync();
        Assert.Equal(2, updates);
        Assert.NotSame(first, changed);
        Assert.Same(changed, service.Current);
    }

    [Fact]
    public async Task Monitor_signal_that_changes_nothing_does_not_republish()
    {
        var runner = new FakeProcessRunner();
        var monitor = new ManualMonitor();
        var service = new PwGraphService(new PwDumpReader(runner), monitor);
        var updates = 0;
        service.GraphUpdated += (_, _) => updates++;

        runner.EnqueueStdout(Fixtures.PwDumpSampleJson);
        await service.StartAsync();
        Assert.Equal(1, updates);

        // The monitor fires but the fresh dump parses identically.
        runner.EnqueueStdout(Fixtures.PwDumpSampleJson);
        monitor.Raise();

        // The signal-driven reload runs on a background continuation; wait for the dump call.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runner.Calls.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(100); // let the parse + dedup after the recorded call finish

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(1, updates);
    }
}
