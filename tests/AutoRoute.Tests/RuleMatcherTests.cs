using System.Linq;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

public class RuleMatcherTests
{
    private static readonly PwGraph Graph = PwDumpReader.Parse(Fixtures.PwDumpSampleJson);

    // The 4 real Zen Stream/Output/Audio streams in the fixture (all application.name=Zen).
    private static readonly int[] ZenStreamIds = { 159, 170, 179, 185 };

    private static MatchCriteria One(Field field, Op op, string value)
        => new(new[] { new Predicate(field, op, value) });

    private static PwNode NodeWithNulls() =>
        new(Id: 9999, NodeName: null, Description: null, MediaClass: null,
            ApplicationName: null, ProcessBinary: null, MediaName: null,
            Ports: System.Array.Empty<PwPort>());

    private readonly RuleMatcher _matcher = new();

    [Fact]
    public void Resolve_ApplicationName_Zen_matches_all_four_streams_app_granularity()
    {
        var matched = _matcher.Resolve(One(Field.ApplicationName, Op.Equals, "Zen"), Graph)
            .Select(n => n.Id).OrderBy(i => i).ToArray();

        // ADR-0003: all of an app's streams count as one — every Zen stream resolves.
        Assert.Equal(ZenStreamIds, matched);
    }

    [Fact]
    public void Resolve_NodeName_GameSink_matches_node_89()
    {
        var matched = _matcher.Resolve(One(Field.NodeName, Op.Equals, "GameSink"), Graph).ToArray();

        var node = Assert.Single(matched);
        Assert.Equal(89, node.Id);
    }

    [Fact]
    public void Equals_is_exact_and_case_sensitive_ordinal()
    {
        // Lower-case "zen" must NOT match application.name "Zen".
        Assert.Empty(_matcher.Resolve(One(Field.ApplicationName, Op.Equals, "zen"), Graph));
    }

    [Fact]
    public void Contains_matches_substring()
    {
        // node.name "GameSink" contains "ameSin".
        var matched = _matcher.Resolve(One(Field.NodeName, Op.Contains, "ameSin"), Graph).ToArray();
        var node = Assert.Single(matched);
        Assert.Equal(89, node.Id);
    }

    [Fact]
    public void Regex_matches_alternation()
    {
        var matched = _matcher.Resolve(One(Field.NodeName, Op.Regex, "^(Game|Music)Sink$"), Graph)
            .Select(n => n.Id).OrderBy(i => i).ToArray();

        Assert.Equal(new[] { 87, 89 }, matched); // MusicSink=87, GameSink=89
    }

    [Fact]
    public void Regex_invalid_pattern_never_throws_and_matches_nothing()
    {
        // Unbalanced group — must be swallowed, not thrown into the reconcile loop.
        Assert.Empty(_matcher.Resolve(One(Field.NodeName, Op.Regex, "(unclosed"), Graph));
    }

    [Fact]
    public void Predicates_are_ANDed()
    {
        var zen159 = Graph.Node(159)!;

        var both = new MatchCriteria(new[]
        {
            new Predicate(Field.ApplicationName, Op.Equals, "Zen"),
            new Predicate(Field.MediaClass, Op.Equals, "Stream/Output/Audio"),
        });
        Assert.True(_matcher.Matches(both, zen159));

        var oneWrong = new MatchCriteria(new[]
        {
            new Predicate(Field.ApplicationName, Op.Equals, "Zen"),
            new Predicate(Field.NodeName, Op.Equals, "GameSink"),
        });
        Assert.False(_matcher.Matches(oneWrong, zen159));
    }

    [Fact]
    public void Null_field_never_matches_any_op()
    {
        var node = NodeWithNulls();

        Assert.False(_matcher.Matches(One(Field.NodeName, Op.Equals, "x"), node));
        Assert.False(_matcher.Matches(One(Field.NodeName, Op.Contains, "x"), node));
        Assert.False(_matcher.Matches(One(Field.NodeName, Op.Regex, ".*"), node)); // conservative
    }

    [Fact]
    public void Empty_criteria_matches_nothing()
    {
        // A rule with no predicates must never resolve to the whole graph.
        Assert.False(_matcher.Matches(MatchCriteria.Empty, Graph.Node(89)!));
        Assert.Empty(_matcher.Resolve(MatchCriteria.Empty, Graph));
    }
}
