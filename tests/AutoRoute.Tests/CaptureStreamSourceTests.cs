using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

/// <summary>
/// An app with both a playback stream and a recording stream (seen live: Legcord/Discord, whose
/// <c>Stream/Input/Audio</c> "RecordStream" exposes <c>monitor_*</c> outputs). The recording stream
/// must never act as a Source: not as the palette card's face (it rendered a blank kind tile), and
/// not as a rule's source (routing "legcord" would echo the mic capture into the target).
/// </summary>
public sealed class CaptureStreamSourceTests
{
    private const int RecordId = 10;   // lower id on purpose: it used to win g.First()
    private const int PlaybackId = 20;

    private static PwGraph LegcordGraph()
    {
        var record = new PwNode(
            RecordId, "legcord", Description: null, MediaClass: "Stream/Input/Audio",
            ApplicationName: "legcord", ProcessBinary: "electron", MediaName: "RecordStream",
            Ports: new List<PwPort>
            {
                new(100, RecordId, PortDirection.Input, "input_FL", "FL", 0),
                new(101, RecordId, PortDirection.Input, "input_FR", "FR", 1),
                new(102, RecordId, PortDirection.Output, "monitor_FL", "FL", 0),
                new(103, RecordId, PortDirection.Output, "monitor_FR", "FR", 1),
            });
        var playback = new PwNode(
            PlaybackId, "legcord", Description: null, MediaClass: "Stream/Output/Audio",
            ApplicationName: "legcord", ProcessBinary: "electron", MediaName: "Playback",
            Ports: new List<PwPort>
            {
                new(200, PlaybackId, PortDirection.Output, "output_FL", "FL", 0),
                new(201, PlaybackId, PortDirection.Output, "output_FR", "FR", 1),
            });

        var nodes = new Dictionary<int, PwNode> { [RecordId] = record, [PlaybackId] = playback };
        var ports = nodes.Values.SelectMany(n => n.Ports).ToDictionary(p => p.Id);
        var graph = new PwGraph(nodes, ports, new Dictionary<int, PwLink>());
        return GraphMutations.WithNullSink(graph, "Discord", "Discord");
    }

    [Fact]
    public void Recording_stream_is_not_a_source()
    {
        var g = LegcordGraph();
        Assert.True(g.Node(RecordId)!.IsCaptureStream);
        Assert.False(NodeRoles.IsAudioSource(g.Node(RecordId)!));
        Assert.True(NodeRoles.IsAudioSource(g.Node(PlaybackId)!));
    }

    [Fact]
    public void Palette_card_is_the_playback_stream_with_an_app_kind()
    {
        var snapshot = BoardModelBuilder.Build(
            LegcordGraph(), RulesDocument.Empty, new RuleMatcher(), new HashSet<string>(), showMonitors: false);

        var legcord = snapshot.Palette.Single(p => p.Title == "legcord");
        Assert.Equal(PlaybackId, legcord.RepresentativeNodeId);
        Assert.Equal(SourceKind.AppStream, legcord.Kind);
        Assert.Equal("Playback", legcord.Subtitle);
        Assert.DoesNotContain(RecordId, legcord.AllNodeIds);
    }

    [Fact]
    public async Task App_rule_links_only_the_playback_stream()
    {
        var g = LegcordGraph();
        var linker = new RecordingLinker();
        var rule = new Rule(
            Id: "legcord-discord", Name: "legcord → Discord", Enabled: true,
            Source: new MatchCriteria(new[] { new Predicate(Field.ApplicationName, Op.Equals, "legcord") }),
            Target: new MatchCriteria(new[] { new Predicate(Field.NodeName, Op.Equals, "Discord") }));
        var doc = RulesDocument.Empty with { Rules = new[] { rule } };

        await new Reconciler(linker, new RuleMatcher()).ReconcileAsync(g, doc);

        Assert.Equal(2, linker.Connects.Count);
        Assert.All(linker.Connects, c => Assert.Equal(PlaybackId, g.Port(c.OutPortId)!.NodeId));
    }
}
