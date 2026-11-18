using System.Collections.Generic;
using System.Linq;
using AutoRoute.App.Services;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Tests;

/// <summary>Saved routes that aren't live right now still show: faded chips and Disconnected rows.</summary>
public sealed class IdleRoutesTests
{
    private static int _port = 5000;

    private static PwNode Sink(int id, string name, string desc) =>
        new(id, name, desc, "Audio/Sink", null, null, null,
            new[] { new PwPort(_port++, id, PortDirection.Input, "playback_FL", "FL", 0),
                    new PwPort(_port++, id, PortDirection.Input, "playback_FR", "FR", 1) });

    private static Rule Route(string id, string name, Field srcField, string src, string targetNode) =>
        new(id, name, true,
            new MatchCriteria(new[] { new Predicate(srcField, Op.Equals, src) }),
            new MatchCriteria(new[] { new Predicate(Field.NodeName, Op.Equals, targetNode) }));

    private static BoardSnapshot Build(PwGraph graph, params Rule[] rules) =>
        BoardModelBuilder.Build(graph, RulesDocument.Empty with { Rules = rules }, new UiRuleMatcher(),
            new HashSet<string>(), showMonitors: false);

    private static PwGraph Graph(params PwNode[] nodes) =>
        new(nodes.ToDictionary(n => n.Id), nodes.SelectMany(n => n.Ports).ToDictionary(p => p.Id), new Dictionary<int, PwLink>());

    [Fact]
    public void A_saved_route_whose_app_is_closed_shows_a_faded_chip_on_its_output()
    {
        var snap = Build(Graph(Sink(10, "Music", "Music")),
            Route("r1", "Spotify → Music", Field.ApplicationName, "Spotify", "Music"));
        var card = Assert.Single(Assert.Single(snap.Columns).Cards);
        Assert.True(card.NotRunning);
        Assert.Equal("Spotify", card.Title);
        Assert.Equal("r1", card.RuleId);
    }

    [Fact]
    public void Routes_to_an_unplugged_device_show_as_a_Disconnected_row()
    {
        var snap = Build(Graph(Sink(10, "Music", "Music")),
            Route("r1", "Zen → PRO X 2 LIGHTSPEED", Field.ApplicationName, "Zen",
                "alsa_output.usb-Logitech_PRO_X_2_LIGHTSPEED-00.analog-stereo"));
        var row = Assert.Single(snap.Columns, c => c.Disconnected);
        Assert.Equal("PRO X 2 LIGHTSPEED", row.Title);
        Assert.Equal("Disconnected", row.Look!.TypeLine);
        Assert.True(Assert.Single(row.Cards).NotRunning);
        Assert.True(row.TargetNodeId < 0); // never a live node id
    }

    [Fact]
    public void A_missing_target_that_is_not_an_output_device_gets_no_ghost_row()
    {
        // e.g. an app's recording stream that closed: nothing to show.
        var rule = new Rule("r1", "Mic → Discord", true,
            new MatchCriteria(new[] { new Predicate(Field.NodeName, Op.Equals, "alsa_input.mic") }),
            new MatchCriteria(new[] { new Predicate(Field.ApplicationName, Op.Equals, "WEBRTC VoiceEngine") }));
        var snap = Build(Graph(Sink(10, "Music", "Music")), rule);
        Assert.DoesNotContain(snap.Columns, c => c.Disconnected);
    }
}
