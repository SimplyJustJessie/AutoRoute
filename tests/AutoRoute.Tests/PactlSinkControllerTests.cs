using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.PipeWire;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

public class PactlSinkControllerTests
{
    [Fact]
    public async Task LoadAsync_builds_exact_pactl_argv_stereo()
    {
        var runner = new FakeProcessRunner().EnqueueStdout("536870913\n");
        var controller = new PactlSinkController(runner);

        var result = await controller.LoadAsync(new NullSinkRequest("GameSink", "Game Sink", Mono: false));

        Assert.True(result.Success);
        Assert.Equal(536870913, result.ModuleIndex);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("pactl", call.FileName);
        Assert.Equal(new[]
        {
            "load-module",
            "module-null-sink",
            "sink_name=GameSink",
            "sink_properties=\"device.description='Game Sink' autoroute.managed=true\"",
        }, call.Arguments);
    }

    [Fact]
    public async Task LoadAsync_mono_adds_channel_args_before_properties()
    {
        var runner = new FakeProcessRunner().EnqueueStdout("42");
        var controller = new PactlSinkController(runner);

        await controller.LoadAsync(new NullSinkRequest("Narration", "Narration", Mono: true));

        var args = Assert.Single(runner.Calls).Arguments;
        Assert.Contains("channels=1", args);
        Assert.Contains("channel_map=mono", args);
        Assert.True(args.ToList().IndexOf("channels=1") < args.ToList().IndexOf(
            "sink_properties=\"device.description='Narration' autoroute.managed=true\""));
    }

    [Fact]
    public async Task LoadAsync_failure_returns_Fail_without_throwing()
    {
        var runner = new FakeProcessRunner().EnqueueFailure("Failure: Module initialization failed");
        var controller = new PactlSinkController(runner);

        var result = await controller.LoadAsync(new NullSinkRequest("X", "X", Mono: false));

        Assert.False(result.Success);
        Assert.Contains("Module initialization failed", result.Error);
    }

    [Fact]
    public async Task LoadAsync_unparseable_index_still_succeeds()
    {
        var runner = new FakeProcessRunner().EnqueueStdout("something unexpected");
        var controller = new PactlSinkController(runner);

        var result = await controller.LoadAsync(new NullSinkRequest("X", "X", Mono: false));

        Assert.True(result.Success);
        Assert.Null(result.ModuleIndex);
    }

    [Fact]
    public async Task UnloadAsync_passes_module_index()
    {
        var runner = new FakeProcessRunner().EnqueueStdout("");
        var controller = new PactlSinkController(runner);

        var result = await controller.UnloadAsync(536870913);

        Assert.True(result.Success);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "unload-module", "536870913" }, call.Arguments);
    }

    [Fact]
    public async Task ListNullSinkModulesAsync_parses_fixture_tolerantly()
    {
        var runner = new FakeProcessRunner()
            .EnqueueStdout(File.ReadAllText(Fixtures.PactlModulesShortPath));
        var controller = new PactlSinkController(runner);

        var modules = await controller.ListNullSinkModulesAsync();

        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "list", "modules", "short" }, call.Arguments);

        // The fixture is the REAL v2-gate capture. Only module-null-sink rows with a parseable
        // sink_name survive; libpipewire modules (including ones whose args span multiple lines)
        // and arg-less pulse modules are all skipped without error.
        Assert.Equal(new[] { "MusicSink", "DiscordSink", "GameSink", "DesktopSink", "autoroute_gate_sink" },
            modules.Select(m => m.SinkName).ToArray());
        Assert.Equal(536870916, modules[0].ModuleIndex);

        // The ownership stamp distinguishes ours (the gate sink) from the user's legacy modules,
        // whose unquoted sink_properties shape must also parse.
        Assert.True(modules.Single(m => m.SinkName == "autoroute_gate_sink").IsAutoRouteTagged);
        Assert.All(modules.Where(m => m.SinkName != "autoroute_gate_sink"),
            m => Assert.False(m.IsAutoRouteTagged));
    }

    [Fact]
    public async Task ListNullSinkModulesAsync_failure_returns_empty_not_throw()
    {
        var runner = new FakeProcessRunner().EnqueueFailure("Connection refused");
        var controller = new PactlSinkController(runner);

        var modules = await controller.ListNullSinkModulesAsync();

        Assert.Empty(modules);
    }
}
