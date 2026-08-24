using System;
using System.Collections.Generic;
using System.Linq;
using AutoRoute.App.Services;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

public class BoardModelBuilderTests
{
    private static readonly IReadOnlySet<string> NoKeptManual = new HashSet<string>();

    [Fact]
    public void Declared_sink_column_is_flagged_managed_and_carries_its_name()
    {
        var graph = GraphMutations.WithNullSink(
            GraphMutations.WithNullSink(PwGraph.Empty, "GameSink", "Game Sink"),
            "OtherSink", "Someone Else's Sink");
        var rules = RulesDocument.Empty with
        {
            VirtualSinks = new[] { new VirtualSinkSpec("v1", "GameSink", "Game Sink", SinkChannels.Stereo) },
        };

        var snapshot = BoardModelBuilder.Build(graph, rules, new RuleMatcher(), NoKeptManual, showMonitors: false);

        var game = snapshot.Columns.Single(c => c.SinkName == "GameSink");
        Assert.True(game.IsManagedSink);

        // A sink in the graph but not declared (another app's / hardware) is a plain column:
        // no managed flag, so no VIRTUAL chip and no delete affordance.
        var other = snapshot.Columns.Single(c => c.SinkName == "OtherSink");
        Assert.False(other.IsManagedSink);
    }

    [Fact]
    public void Duplicate_named_sinks_get_unique_column_keys()
    {
        // Seen live: a legacy static conf and the generated drop-in both boot the same sink →
        // two nodes share node.name. Column keys must stay unique (duplicates crashed the
        // ViewModel diff-merge dictionaries and aborted the app).
        var graph = GraphMutations.WithNullSink(
            GraphMutations.WithNullSink(PwGraph.Empty, "DiscordSink", "Discord Sink"),
            "DiscordSink", "Discord Sink");

        var snapshot = BoardModelBuilder.Build(
            graph, RulesDocument.Empty, new RuleMatcher(), NoKeptManual, showMonitors: false);

        Assert.Equal(2, snapshot.Columns.Count);
        Assert.Equal(2, snapshot.Columns.Select(c => c.Key).Distinct().Count());
        // The first occurrence keeps the plain identity so diff-merge stays stable normally.
        Assert.Contains(snapshot.Columns, c => c.Key == "DiscordSink");
    }

    [Fact]
    public void Video_kind_surfaces_only_video_nodes_audio_kind_surfaces_only_audio_nodes()
    {
        // A mixed graph: one audio sink (GameSink) plus a Spout2PW-shaped video source/target pair
        // (VTube Studio → OBS) — the two boards must partition it cleanly, neither leaking into
        // the other's columns/palette.
        var graph = GraphMutations.WithVideoTarget(
            GraphMutations.WithVideoSource(
                GraphMutations.WithNullSink(PwGraph.Empty, "GameSink", "Game Sink"),
                "spout2pw", applicationName: "spout2pw", mediaName: "VTube Studio"),
            "OBS Studio", applicationName: "OBS Studio");

        var audio = BoardModelBuilder.Build(graph, RulesDocument.Empty, new RuleMatcher(), NoKeptManual, showMonitors: false, MediaKind.Audio);
        Assert.Contains(audio.Columns, c => c.SinkName == "GameSink");
        Assert.DoesNotContain(audio.Columns, c => c.Title == "OBS Studio");
        Assert.DoesNotContain(audio.Palette, p => p.Title == "spout2pw");

        var video = BoardModelBuilder.Build(graph, RulesDocument.Empty, new RuleMatcher(), NoKeptManual, showMonitors: false, MediaKind.Video);
        Assert.DoesNotContain(video.Columns, c => c.SinkName == "GameSink");
        Assert.Contains(video.Columns, c => c.Title == "OBS Studio");
        Assert.Contains(video.Palette, p => p.Title == "spout2pw");
    }
}
