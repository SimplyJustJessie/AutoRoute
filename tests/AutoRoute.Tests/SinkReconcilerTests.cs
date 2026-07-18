using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace AutoRoute.Tests;

public sealed class SinkReconcilerTests : IDisposable
{
    private readonly string _dir;
    private readonly FakeSinkController _controller = new();
    private readonly FakeTimeProvider _time = new();
    private readonly SinkReconciler _reconciler;

    public SinkReconcilerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "autoroute-tests", Guid.NewGuid().ToString("N"));
        var writer = new SinkDropInWriter(Path.Combine(_dir, SinkDropInWriter.FileName));
        _reconciler = new SinkReconciler(_controller, writer, log: null, _time);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static RulesDocument Declare(params VirtualSinkSpec[] sinks)
        => RulesDocument.Empty with { VirtualSinks = sinks };

    private static VirtualSinkSpec Spec(string name, SinkChannels channels = SinkChannels.Stereo)
        => new($"id-{name}", name, name, channels);

    [Fact]
    public async Task Declared_and_present_in_graph_loads_nothing()
    {
        var graph = GraphMutations.WithNullSink(PwGraph.Empty, "GameSink");
        var rules = Declare(Spec("GameSink"));

        await _reconciler.EnsureAsync(graph, rules);

        Assert.Empty(_controller.Loads);
        Assert.Empty(_controller.Unloads);
    }

    [Fact]
    public async Task Declared_but_absent_lists_then_loads_once()
    {
        var rules = Declare(Spec("GameSink"));

        await _reconciler.EnsureAsync(PwGraph.Empty, rules);

        Assert.Equal(1, _controller.ListCalls);
        var load = Assert.Single(_controller.Loads);
        Assert.Equal("GameSink", load.Name);
        Assert.False(load.Mono);
    }

    [Fact]
    public async Task Mono_spec_requests_mono_sink()
    {
        await _reconciler.EnsureAsync(PwGraph.Empty, Declare(Spec("Narration", SinkChannels.Mono)));

        Assert.True(Assert.Single(_controller.Loads).Mono);
    }

    [Fact]
    public async Task Module_loaded_but_node_not_yet_in_snapshot_skips_load()
    {
        // The drop-in (or a previous pass) already loaded the module; the node just hasn't landed
        // in this snapshot yet. Loading again would create a duplicate sink.
        _controller.Modules.Add(new NullSinkModule(7, "GameSink", "sink_name=GameSink autoroute.managed=true"));

        await _reconciler.EnsureAsync(PwGraph.Empty, Declare(Spec("GameSink")));

        Assert.Empty(_controller.Loads);
    }

    [Fact]
    public async Task Failed_load_backs_off_then_retries_after_delay()
    {
        _controller.FailLoads.Add("GameSink");
        var rules = Declare(Spec("GameSink"));

        await _reconciler.EnsureAsync(PwGraph.Empty, rules);
        Assert.Single(_controller.Loads);

        // Immediately after the failure: in backoff, no further attempt.
        await _reconciler.EnsureAsync(PwGraph.Empty, rules);
        Assert.Single(_controller.Loads);

        // First backoff window is 5s — after it elapses the load is retried.
        _time.Advance(TimeSpan.FromSeconds(6));
        await _reconciler.EnsureAsync(PwGraph.Empty, rules);
        Assert.Equal(2, _controller.Loads.Count);
    }

    [Fact]
    public async Task Stale_tagged_module_is_unloaded_when_undeclared()
    {
        _controller.Modules.Add(new NullSinkModule(9, "OldSink",
            "sink_name=OldSink sink_properties=\"device.description='Old' autoroute.managed=true\""));

        await _reconciler.EnsureAsync(PwGraph.Empty, Declare(Spec("GameSink")));

        Assert.Contains(9, _controller.Unloads);
    }

    [Fact]
    public async Task Untagged_module_is_never_unloaded()
    {
        // The user's legacy static-conf sink: same name shape, no autoroute.managed stamp.
        _controller.Modules.Add(new NullSinkModule(9, "LegacySink", "sink_name=LegacySink"));

        await _reconciler.EnsureAsync(PwGraph.Empty, Declare(Spec("GameSink")));

        Assert.Empty(_controller.Unloads);
    }

    [Fact]
    public async Task Steady_state_with_unchanged_rules_spawns_no_processes()
    {
        var graph = GraphMutations.WithNullSink(PwGraph.Empty, "GameSink");
        var rules = Declare(Spec("GameSink"));

        await _reconciler.EnsureAsync(graph, rules);   // first pass: stale check runs
        var listsAfterFirst = _controller.ListCalls;

        // Graph-update-triggered passes with the same rules document and nothing missing.
        await _reconciler.EnsureAsync(graph, rules);
        await _reconciler.EnsureAsync(graph, rules);

        Assert.Equal(listsAfterFirst, _controller.ListCalls);
    }

    [Fact]
    public async Task Drop_in_is_synced_every_pass()
    {
        var path = Path.Combine(_dir, SinkDropInWriter.FileName);
        var graph = GraphMutations.WithNullSink(PwGraph.Empty, "GameSink");

        await _reconciler.EnsureAsync(graph, Declare(Spec("GameSink")));
        Assert.Contains("sink_name=GameSink", await File.ReadAllTextAsync(path));

        // Removing the declaration deletes the drop-in on the next pass.
        await _reconciler.EnsureAsync(graph, Declare());
        Assert.False(File.Exists(path));
    }
}
