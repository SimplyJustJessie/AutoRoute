using System.Text.Json;
using System.Threading.Tasks;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using AutoRoute.PipeWire.Process;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

public class PwDumpReaderResilienceTests
{
    // A link carrying the ownership tag exactly as pw-link -p would stamp it.
    private const string TaggedLinkJson = """
    [
      { "id": 500, "type": "PipeWire:Interface:Link",
        "info": { "output-node-id": 10, "output-port-id": 11,
                  "input-node-id": 20, "input-port-id": 21, "state": "active",
                  "props": { "autoroute.managed": "true", "autoroute.rule": "games-to-gamesink",
                             "object.id": 500 } } }
    ]
    """;

    [Fact]
    public void Reads_ownership_tag_off_link_props()
    {
        var g = PwDumpReader.Parse(TaggedLinkJson);
        var link = g.Link(500)!;
        Assert.True(link.IsManaged);
        Assert.Equal("games-to-gamesink", link.RuleId);
    }

    [Fact]
    public void Reads_ownership_tag_from_REAL_gate_dump_where_managed_is_a_json_boolean()
    {
        // Captured live during the Milestone-2 ownership gate: pw-dump coerces the string "true"
        // we pass via pw-link -p into a JSON boolean `true`. The model must still read it as owned.
        var g = PwDumpReader.Parse(Fixtures.GateTaggedLinkJson);
        var link = g.Link(218)!;
        Assert.True(link.IsManaged);
        Assert.Equal("gate-test", link.RuleId);
    }

    [Fact]
    public void Reads_REAL_pactl_created_null_sink_node_from_gate_capture()
    {
        // Captured live during the v2 gate (M1): the node a `pactl load-module module-null-sink`
        // with our sink_properties stamp produces. Its props carry `autoroute.managed` as a JSON
        // boolean and assorted non-string values (ints, nested arrays as strings) — all of which
        // must parse without error into the stable-key model.
        var g = PwDumpReader.Parse("[" + System.IO.File.ReadAllText(Fixtures.PwDumpManagedSinkPath) + "]");

        var node = Assert.Single(g.Nodes);
        Assert.Equal("autoroute_gate_sink", node.NodeName);
        Assert.Equal("Audio/Sink", node.MediaClass);
        Assert.Equal("AR Gate", node.Description);
    }

    [Fact]
    public void Parse_throws_on_malformed_json()
    {
        Assert.ThrowsAny<JsonException>(() => PwDumpReader.Parse("{ this is not valid"));
    }

    [Fact]
    public async Task LoadAsync_keeps_last_good_on_malformed_json()
    {
        var runner = new FakeProcessRunner()
            .EnqueueStdout(Fixtures.PwDumpSampleJson)   // first load: good
            .EnqueueStdout("{ broken json ][");          // second load: torn read
        var reader = new PwDumpReader(runner);

        var good = await reader.LoadAsync();
        Assert.Equal(31, good.NodesById.Count);

        var afterTear = await reader.LoadAsync();
        // Same instance kept — never crashed, never emptied.
        Assert.Same(good, afterTear);
        Assert.Equal(31, afterTear.NodesById.Count);
    }

    [Fact]
    public async Task LoadAsync_throws_PwToolException_on_nonzero_exit()
    {
        var runner = new FakeProcessRunner().EnqueueFailure("pw-dump: connection refused", exit: 1);
        var reader = new PwDumpReader(runner);
        await Assert.ThrowsAsync<PwToolException>(async () => await reader.LoadAsync());
    }
}
