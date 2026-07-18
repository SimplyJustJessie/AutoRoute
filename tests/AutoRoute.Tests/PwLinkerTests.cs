using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

public class PwLinkerTests
{
    [Fact]
    public void BuildManagedProps_emits_valid_tag_json()
    {
        var json = PwLinker.BuildManagedProps("games-to-gamesink");
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

        Assert.Equal("true", map[PwLink.ManagedPropKey]);
        Assert.Equal("games-to-gamesink", map[PwLink.RulePropKey]);
    }

    [Fact]
    public async Task ConnectAsync_invokes_pw_link_with_props_and_numeric_ids()
    {
        var runner = new FakeProcessRunner().EnqueueStdout("");
        var linker = new PwLinker(runner);

        var result = await linker.ConnectAsync(190, 92, "music-rule");

        Assert.True(result.Success);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("pw-link", call.FileName);
        Assert.Contains("-p", call.Arguments);
        Assert.Contains("190", call.Arguments);
        Assert.Contains("92", call.Arguments);
        // The props token must be the parsed ownership tag JSON.
        var args = call.Arguments.ToList();
        var propsIdx = args.IndexOf("-p") + 1;
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(args[propsIdx])!;
        Assert.Equal("true", map[PwLink.ManagedPropKey]);
        Assert.Equal("music-rule", map[PwLink.RulePropKey]);
    }

    [Fact]
    public async Task ConnectAsync_returns_failure_without_throwing_when_port_vanished()
    {
        var runner = new FakeProcessRunner().EnqueueFailure("failed to link ports: No such port", exit: 1);
        var linker = new PwLinker(runner);

        var result = await linker.ConnectAsync(9990, 9991, "r");

        Assert.False(result.Success);
        Assert.Contains("No such port", result.Error);
    }

    [Fact]
    public async Task DisconnectAsync_by_link_id_uses_dash_d()
    {
        var runner = new FakeProcessRunner().EnqueueStdout("");
        var linker = new PwLinker(runner);

        await linker.DisconnectAsync(172);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "-d", "172" }, call.Arguments);
    }

    [Fact]
    public async Task DisconnectAsync_by_port_pair_passes_both_ids()
    {
        var runner = new FakeProcessRunner().EnqueueStdout("");
        var linker = new PwLinker(runner);

        await linker.DisconnectAsync(190, 92);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "-d", "190", "92" }, call.Arguments);
    }
}
