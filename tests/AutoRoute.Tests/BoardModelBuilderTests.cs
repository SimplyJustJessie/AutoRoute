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
}
