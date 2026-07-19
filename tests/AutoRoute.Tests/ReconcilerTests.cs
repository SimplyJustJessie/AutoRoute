using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

public class ReconcilerTests
{
    private static PwGraph BaseGraph() => PwDumpReader.Parse(Fixtures.PwDumpSampleJson);

    private static readonly int[] ZenStreamIds = { 159, 170, 179, 185 };
    private const int GameSinkId = 89;
    private const int HeadsetId = 55; // alsa PRO X 2 — the default sink the Zen streams feed
    private const string HeadsetName =
        "alsa_output.usb-Logitech_PRO_X_2_LIGHTSPEED_0000000000000000-00.analog-stereo";

    private static MatchCriteria Crit(Field field, Op op, string value)
        => new(new[] { new Predicate(field, op, value) });

    private static Rule ZenToGameSink(bool enabled = true) => new(
        Id: "zen-game", Name: "Zen → GameSink", Enabled: enabled,
        Source: Crit(Field.ApplicationName, Op.Equals, "Zen"),
        Target: Crit(Field.NodeName, Op.Equals, "GameSink"));

    private static RulesDocument Doc(
        IEnumerable<Rule>? rules = null,
        IEnumerable<Suppression>? suppressions = null,
        IEnumerable<ProtectedMatch>? protectedMatches = null)
        => new(
            RulesDocument.CurrentVersion,
            (rules ?? Enumerable.Empty<Rule>()).ToList(),
            (suppressions ?? Enumerable.Empty<Suppression>()).ToList(),
            (protectedMatches ?? Enumerable.Empty<ProtectedMatch>()).ToList(),
            Array.Empty<VirtualSinkSpec>());

    private static (Reconciler reconciler, RecordingLinker linker) NewReconciler()
    {
        var linker = new RecordingLinker();
        return (new Reconciler(linker, new RuleMatcher()), linker);
    }

    // ---- Step 1/3: desired-vs-actual create set --------------------------------------------------

    [Fact]
    public async Task Creates_channel_correct_managed_links_for_rule_and_nothing_else()
    {
        var graph = BaseGraph();
        var (reconciler, linker) = NewReconciler();

        await reconciler.ReconcileAsync(graph, Doc(rules: new[] { ZenToGameSink() }));

        // 4 Zen streams x 2 channels (FL, FR) = 8 links, no deletions (no managed/suppressed links).
        Assert.Equal(8, linker.Connects.Count);
        Assert.Empty(linker.Disconnects);

        foreach (var c in linker.Connects)
        {
            var outPort = graph.Port(c.OutPortId)!;
            var inPort = graph.Port(c.InPortId)!;
            Assert.Contains(outPort.NodeId, ZenStreamIds);       // source is a Zen stream
            Assert.Equal(GameSinkId, inPort.NodeId);              // target is GameSink
            Assert.Equal(outPort.Channel, inPort.Channel);       // FL->FL, FR->FR
            Assert.Equal("zen-game", c.RuleId);                  // tagged with the rule id
        }

        // Exactly two links (FL + FR) per Zen stream, all distinct pairs.
        Assert.Equal(8, linker.Connects.Select(c => (c.OutPortId, c.InPortId)).Distinct().Count());
        foreach (var zen in ZenStreamIds)
            Assert.Equal(2, linker.Connects.Count(c => graph.Port(c.OutPortId)!.NodeId == zen));
    }

    [Fact]
    public async Task Disabled_rule_produces_no_links()
    {
        var (reconciler, linker) = NewReconciler();
        await reconciler.ReconcileAsync(BaseGraph(), Doc(rules: new[] { ZenToGameSink(enabled: false) }));
        Assert.Equal(0, linker.TotalOps);
    }

    // ---- Step 3/4: idempotency -------------------------------------------------------------------

    [Fact]
    public async Task Second_reconcile_over_realized_graph_is_a_no_op()
    {
        var graph = BaseGraph();
        var doc = Doc(rules: new[] { ZenToGameSink() });

        // First pass records the creates.
        var (first, firstLinker) = NewReconciler();
        await first.ReconcileAsync(graph, doc);
        var created = firstLinker.Connects.Select(c => (c.OutPortId, c.InPortId)).ToList();
        Assert.Equal(8, created.Count);

        // Realize them as managed links (what the next pw-dump snapshot would show), then reconcile.
        var realized = GraphMutations.WithManagedLinks(graph, created, "zen-game");

        var (second, secondLinker) = NewReconciler();
        await second.ReconcileAsync(realized, doc);
        Assert.Equal(0, secondLinker.TotalOps); // desired state already achieved → zero ops

        // And a third pass stays a no-op.
        var (third, thirdLinker) = NewReconciler();
        await third.ReconcileAsync(realized, doc);
        Assert.Equal(0, thirdLinker.TotalOps);
    }

    // ---- Flagship: rule reattaches to renumbered ports after a relaunch ---------------------------

    [Fact]
    public async Task Flagship_links_reappear_on_new_ports_after_relaunch()
    {
        var doc = Doc(rules: new[] { ZenToGameSink() });

        // Baseline create set (old ephemeral port ids).
        var baseGraph = BaseGraph();
        var (baseRec, baseLinker) = NewReconciler();
        await baseRec.ReconcileAsync(baseGraph, doc);
        var baseOutPorts = baseLinker.Connects.Select(c => c.OutPortId).OrderBy(i => i).ToArray();

        // Simulate a relaunch: every Zen node + its ports get fresh ids (+10000).
        const int offset = 10_000;
        var relaunched = GraphMutations.RenumberNodes(baseGraph, ZenStreamIds.ToHashSet(), offset);

        var (rec, linker) = NewReconciler();
        await rec.ReconcileAsync(relaunched, doc);

        // Same shape (8 links to GameSink), but now targeting the NEW Zen output ports.
        Assert.Equal(8, linker.Connects.Count);
        Assert.Empty(linker.Disconnects);

        var newZenIds = ZenStreamIds.Select(id => id + offset).ToHashSet();
        foreach (var c in linker.Connects)
        {
            var outPort = relaunched.Port(c.OutPortId)!;
            var inPort = relaunched.Port(c.InPortId)!;
            Assert.Contains(outPort.NodeId, newZenIds);       // resolved the reincarnated Zen streams
            Assert.Equal(GameSinkId, inPort.NodeId);
            Assert.Equal(outPort.Channel, inPort.Channel);
        }

        // Proof the ports genuinely changed: the new out-port set is disjoint from the old one.
        var newOutPorts = linker.Connects.Select(c => c.OutPortId).OrderBy(i => i).ToArray();
        Assert.Empty(baseOutPorts.Intersect(newOutPorts));
        Assert.Equal(baseOutPorts.Select(p => p + offset).ToArray(), newOutPorts);
    }

    // ---- Step 4: stale managed cleanup, never touching unowned links -----------------------------

    [Fact]
    public async Task Stale_managed_link_is_removed_and_unowned_links_are_untouched()
    {
        // A managed Zen->GameSink link exists, but there is no rule → it is stale.
        var baseGraph = BaseGraph();
        var zenOut = baseGraph.Node(159)!.OutputPorts.First(p => p.Channel == "FL").Id;
        var gameIn = baseGraph.Node(GameSinkId)!.InputPorts.First(p => p.Channel == "FL").Id;
        var graph = GraphMutations.WithManagedLinks(baseGraph, new[] { (zenOut, gameIn) }, "orphan");

        var staleLinkId = graph.ManagedLinks.Single().Id;
        var unownedBefore = graph.UnownedLinks.Select(l => l.Id).ToHashSet();

        var (reconciler, linker) = NewReconciler();
        await reconciler.ReconcileAsync(graph, Doc()); // empty policy

        Assert.Empty(linker.Connects);
        var disc = Assert.Single(linker.Disconnects);
        Assert.Equal(staleLinkId, disc.LinkId);

        // No unowned (user/WirePlumber) link was disconnected.
        Assert.DoesNotContain(linker.Disconnects, d => d.LinkId is int id && unownedBefore.Contains(id));
    }

    // ---- Step 5: suppressions delete even unowned links ------------------------------------------

    [Fact]
    public async Task Suppression_deletes_matching_unowned_links()
    {
        var graph = BaseGraph();

        // Every Zen->headset link in the fixture is an unowned (WirePlumber) link.
        var expected = graph.Links
            .Where(l => ZenStreamIds.Contains(l.OutNodeId) && l.InNodeId == HeadsetId)
            .Select(l => l.Id).ToHashSet();
        Assert.NotEmpty(expected);
        Assert.All(expected, id => Assert.False(graph.Link(id)!.IsManaged)); // they are unowned

        var suppression = new Suppression(
            "no-zen-headset",
            Crit(Field.ApplicationName, Op.Equals, "Zen"),
            Crit(Field.NodeName, Op.Equals, HeadsetName));

        var (reconciler, linker) = NewReconciler();
        await reconciler.ReconcileAsync(graph, Doc(suppressions: new[] { suppression }));

        Assert.Empty(linker.Connects);
        Assert.All(linker.Disconnects, d => Assert.NotNull(d.LinkId)); // deleted by link id
        var actuallyDisconnected = linker.Disconnects.Select(d => d.LinkId!.Value).ToHashSet();
        Assert.Equal(expected, actuallyDisconnected);
    }

    // ---- Precedence guard: coexisting positive + suppression for the same pair must not flap -----

    [Fact]
    public async Task Coexisting_positive_and_suppression_for_same_pair_does_not_flap()
    {
        // Out-of-contract hand edit: rules.json names BOTH a positive rule and a Suppression for
        // Zen -> GameSink. Suppression must win at desired-set build time (no create-then-delete).
        var doc = Doc(
            rules: new[] { ZenToGameSink() },
            suppressions: new[]
            {
                new Suppression("no-zen-game",
                    Crit(Field.ApplicationName, Op.Equals, "Zen"),
                    Crit(Field.NodeName, Op.Equals, "GameSink")),
            });

        // A managed Zen -> GameSink link already exists from a previous cycle.
        var baseGraph = BaseGraph();
        var zenOut = baseGraph.Node(159)!.OutputPorts.First(p => p.Channel == "FL").Id;
        var gameIn = baseGraph.Node(GameSinkId)!.InputPorts.First(p => p.Channel == "FL").Id;
        var graph = GraphMutations.WithManagedLinks(baseGraph, new[] { (zenOut, gameIn) }, "zen-game");
        var preexisting = graph.ManagedLinks.Single().Id;

        var (reconciler, linker) = NewReconciler();
        await reconciler.ReconcileAsync(graph, doc);

        // No creates at all — the pair never enters the desired set (this is the anti-flap property;
        // without the guard the positive rule would create the other 7 channel pairs here).
        Assert.Empty(linker.Connects);
        // The pre-existing link ends deleted, exactly once (steps 4 & 5 de-dupe).
        var disc = Assert.Single(linker.Disconnects);
        Assert.Equal(preexisting, disc.LinkId);

        // Second reconcile over the realized graph (link now gone) is a no-op.
        var realized = BaseGraph(); // no managed Zen -> GameSink link present
        var (second, secondLinker) = NewReconciler();
        await second.ReconcileAsync(realized, doc);
        Assert.Equal(0, secondLinker.TotalOps);
    }

    // ---- Step 5/precedence: Protected overrides both positive rules and suppressions -------------

    [Fact]
    public async Task Protected_source_is_never_linked_or_suppressed()
    {
        var graph = BaseGraph();
        var doc = Doc(
            rules: new[] { ZenToGameSink() },
            suppressions: new[]
            {
                new Suppression("no-zen-headset",
                    Crit(Field.ApplicationName, Op.Equals, "Zen"),
                    Crit(Field.NodeName, Op.Equals, HeadsetName)),
            },
            protectedMatches: new[]
            {
                new ProtectedMatch("keep-zen", Crit(Field.ApplicationName, Op.Equals, "Zen")),
            });

        var (reconciler, linker) = NewReconciler();
        await reconciler.ReconcileAsync(graph, doc);

        // Zen is Protected: no managed links created FROM it, and its suppressed links are left alone.
        Assert.Equal(0, linker.TotalOps);
    }

    [Fact]
    public async Task Protected_target_blocks_link_creation()
    {
        var graph = BaseGraph();
        var doc = Doc(
            rules: new[] { ZenToGameSink() },
            protectedMatches: new[]
            {
                new ProtectedMatch("keep-gamesink", Crit(Field.NodeName, Op.Equals, "GameSink")),
            });

        var (reconciler, linker) = NewReconciler();
        await reconciler.ReconcileAsync(graph, doc);

        Assert.Equal(0, linker.TotalOps); // GameSink is a Protected target → no links to it
    }
}
