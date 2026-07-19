using System.Collections.Generic;
using System.Linq;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

/// <summary>
/// <see cref="PwGraph.StructurallyEquals"/> is what lets the graph service swallow param-only
/// churn (the CPU hot path): two dumps that differ only in fields nobody reads must compare
/// equal, and any change a consumer could observe must compare unequal.
/// </summary>
public class PwGraphStructuralEqualityTests
{
    private static PwGraph RealGraph() => PwDumpReader.Parse(Fixtures.PwDumpSampleJson);

    [Fact]
    public void Two_parses_of_the_same_dump_are_equal()
    {
        Assert.True(RealGraph().StructurallyEquals(RealGraph()));
    }

    [Fact]
    public void Link_state_flips_compare_equal()
    {
        // Streams idling/resuming flip link state constantly; nothing consumes it.
        var a = RealGraph();
        var links = a.LinksById.ToDictionary(kv => kv.Key, kv => kv.Value);
        var anyId = links.Keys.First();
        links[anyId] = links[anyId] with { State = links[anyId].State == "paused" ? "active" : "paused" };
        var b = new PwGraph(a.NodesById, a.PortsById, links);

        Assert.True(a.StructurallyEquals(b));
    }

    [Fact]
    public void A_new_managed_link_compares_unequal()
    {
        var a = RealGraph();
        var outPort = a.Ports.First(p => p.IsOutput).Id;
        var inPort = a.Ports.First(p => p.IsInput).Id;
        var b = GraphMutations.WithManagedLinks(a, new[] { (outPort, inPort) }, "rule-1");

        Assert.False(a.StructurallyEquals(b));
        Assert.False(b.StructurallyEquals(a));
    }

    [Fact]
    public void Losing_the_managed_tag_compares_unequal()
    {
        // Same link id and endpoints, different ownership — the reconciler must see this.
        var outPort = RealGraph().Ports.First(p => p.IsOutput).Id;
        var inPort = RealGraph().Ports.First(p => p.IsInput).Id;
        var a = GraphMutations.WithManagedLinks(RealGraph(), new[] { (outPort, inPort) }, "rule-1");

        var links = a.LinksById.ToDictionary(kv => kv.Key, kv => kv.Value);
        var managedId = links.Values.First(l => l.IsManaged).Id;
        links[managedId] = links[managedId] with { Props = new Dictionary<string, string>() };
        var b = new PwGraph(a.NodesById, a.PortsById, links);

        Assert.False(a.StructurallyEquals(b));
    }

    [Fact]
    public void A_changed_stable_key_prop_compares_unequal()
    {
        // media.name feeds card subtitles — a tab/track change must reach the UI.
        var a = RealGraph();
        var nodes = a.NodesById.ToDictionary(kv => kv.Key, kv => kv.Value);
        var stream = nodes.Values.First(n => n.MediaName is not null);
        nodes[stream.Id] = stream with { MediaName = stream.MediaName + " (changed)" };
        var b = new PwGraph(nodes, a.PortsById, a.LinksById);

        Assert.False(a.StructurallyEquals(b));
    }

    [Fact]
    public void An_added_or_removed_node_compares_unequal()
    {
        var a = RealGraph();
        var b = GraphMutations.WithNullSink(a, "NewSink");
        Assert.False(a.StructurallyEquals(b));

        var nodes = a.NodesById.ToDictionary(kv => kv.Key, kv => kv.Value);
        nodes.Remove(nodes.Keys.First());
        var c = new PwGraph(nodes, a.PortsById, a.LinksById);
        Assert.False(a.StructurallyEquals(c));
    }

    [Fact]
    public void Renumbered_ephemeral_ids_compare_unequal()
    {
        // A relaunch regenerates ids — that IS a structural change (rules must re-resolve).
        var a = RealGraph();
        var streamId = a.Nodes.First(n => n.MediaClass == "Stream/Output/Audio").Id;
        var b = GraphMutations.RenumberNodes(a, new HashSet<int> { streamId }, 10_000);

        Assert.False(a.StructurallyEquals(b));
    }

    [Fact]
    public void Empty_graphs_compare_equal()
    {
        Assert.True(PwGraph.Empty.StructurallyEquals(
            new PwGraph(
                new Dictionary<int, PwNode>(),
                new Dictionary<int, PwPort>(),
                new Dictionary<int, PwLink>())));
    }
}
