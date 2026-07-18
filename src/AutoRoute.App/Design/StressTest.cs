using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Design;

/// <summary>
/// Window-free performance + memory-leak harness (run via <c>--stress</c>). Three phases:
///
///  1. <b>Board build perf</b> — times <see cref="BoardModelBuilder.Build"/> over a large synthetic
///     graph (the hot path behind every GraphUpdated).
///  2. <b>Reconcile perf</b> — times <see cref="Reconciler.ReconcileAsync"/> over the same graph
///     with a no-op linker.
///  3. <b>Leak check</b> — churns a live <see cref="BoardViewModel"/> through hundreds of graph
///     generations (apps restarting with fresh ephemeral ids, sinks coming and going), then proves
///     removed ViewModels are collectable (WeakReference) and managed memory returns to baseline.
///
/// Exit code 0 = all checks green. Runs in CI after the smoke checks.
/// </summary>
public static class StressTest
{
    private const int Sinks = 40;
    private const int Apps = 120;
    private const int Captures = 12;

    public static int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string label)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label);
            if (!ok) failures.Add(label);
        }

        var graph = BigGraph(0);
        var rules = BigRules();
        Console.WriteLine($"[stress] graph: {graph.Nodes.Count} nodes, {graph.Ports.Count} ports, " +
                          $"{graph.Links.Count} links; policy: {rules.Rules.Count} rules, " +
                          $"{rules.Suppressions.Count} suppressions, {rules.Protected.Count} protected");

        // ---- Phase 1: BoardModelBuilder.Build ------------------------------------------------
        var uiMatcher = new UiRuleMatcher();
        var kept = new HashSet<string>();
        var buildMs = Time(300, () => BoardModelBuilder.Build(graph, rules, uiMatcher, kept, showMonitors: true));
        Console.WriteLine($"[stress] BoardModelBuilder.Build: {buildMs:F3} ms/build");
        Check(buildMs < 50, $"board build stays under 50 ms ({buildMs:F3} ms)");

        // ---- Phase 2: Reconciler.ReconcileAsync ----------------------------------------------
        var reconciler = new Reconciler(new NoopLinker(), new RuleMatcher());
        var reconcileMs = Time(300, () => reconciler.ReconcileAsync(graph, rules).GetAwaiter().GetResult());
        Console.WriteLine($"[stress] Reconciler.ReconcileAsync: {reconcileMs:F3} ms/cycle");
        Check(reconcileMs < 50, $"reconcile stays under 50 ms ({reconcileMs:F3} ms)");

        // ---- Phase 3: live-board churn + leak check ------------------------------------------
        var (churnMs, leakedVms, memGrowthKb) = ChurnBoard(generations: 300);
        Console.WriteLine($"[stress] live churn: {churnMs:F3} ms/graph-update; " +
                          $"memory growth after churn: {memGrowthKb} KB; leaked VMs: {leakedVms}");
        Check(leakedVms == 0, $"removed ViewModels are collectable ({leakedVms} still alive)");
        Check(memGrowthKb < 3072, $"managed memory returns to baseline (+{memGrowthKb} KB, limit 3072)");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("STRESS: PASS (perf + leak checks green)");
            return 0;
        }
        Console.WriteLine($"STRESS: FAIL ({failures.Count} check(s) failed)");
        return 1;
    }

    // ---- phase 3 ------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (double msPerUpdate, int leakedVms, long memGrowthKb) ChurnBoard(int generations)
    {
        var service = new MockPwGraphService(BigGraph(0));
        var board = new BoardViewModel(
            service, new NoopLinker(), new MockRuleStore(BigRules()), new MockReconciler(), new UiRuleMatcher());
        board.InitializeAsync().GetAwaiter().GetResult();

        // Warm-up churn, then baseline memory under full GC.
        for (var g = 1; g <= 30; g++) service.Publish(BigGraph(g));
        var baseline = FullGcMemory();

        var sw = Stopwatch.StartNew();
        for (var g = 31; g <= generations; g++) service.Publish(BigGraph(g));
        sw.Stop();

        // Grab weak refs to every live column/card/palette VM, then empty the graph — everything
        // must become unreachable (the board itself stays alive, its collections must let go).
        var weakRefs = CollectVmRefs(board);
        service.Publish(PwGraph.Empty);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var leaked = weakRefs.Count(w => w.IsAlive);
        var growthKb = (FullGcMemory() - baseline) / 1024;

        GC.KeepAlive(board);
        return (sw.Elapsed.TotalMilliseconds / (generations - 30), leaked, growthKb);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> CollectVmRefs(BoardViewModel board)
    {
        var refs = new List<WeakReference>();
        foreach (var col in board.Columns)
        {
            refs.Add(new WeakReference(col));
            foreach (var card in col.Cards) refs.Add(new WeakReference(card));
        }
        foreach (var item in board.Palette.Items) refs.Add(new WeakReference(item));
        return refs;
    }

    private static long FullGcMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static double Time(int iterations, Action action)
    {
        for (var i = 0; i < 20; i++) action(); // warm-up / JIT
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations;
    }

    // ---- synthetic graph ----------------------------------------------------------------------

    /// <summary>
    /// A deterministic "busy desktop" graph. <paramref name="generation"/> simulates churn the way
    /// PipeWire does it: app/stream nodes get fresh ephemeral ids every generation and a rotating
    /// third of the apps is absent; sinks/captures keep stable ids (hardware doesn't restart).
    /// </summary>
    private static PwGraph BigGraph(int generation)
    {
        var nodes = new Dictionary<int, PwNode>();
        var ports = new Dictionary<int, PwPort>();
        var links = new Dictionary<int, PwLink>();
        var portId = 100_000 + generation * 10_000; // fresh port ids per generation

        int NextPort() => portId++;

        PwNode Sink(int id, string name, string desc)
        {
            var list = new List<PwPort>
            {
                new(NextPort(), id, PortDirection.Input, "playback_FL", "FL", 0),
                new(NextPort(), id, PortDirection.Input, "playback_FR", "FR", 1),
                new(NextPort(), id, PortDirection.Output, "monitor_FL", "FL", 2),
                new(NextPort(), id, PortDirection.Output, "monitor_FR", "FR", 3),
            };
            var n = new PwNode(id, name, desc, "Audio/Sink", null, null, null, list);
            nodes[id] = n;
            foreach (var p in list) ports[p.Id] = p;
            return n;
        }

        PwNode App(int id, string app, string media)
        {
            var list = new List<PwPort>
            {
                new(NextPort(), id, PortDirection.Output, "output_FL", "FL", 0),
                new(NextPort(), id, PortDirection.Output, "output_FR", "FR", 1),
            };
            var n = new PwNode(id, app.ToLowerInvariant(), null, "Stream/Output/Audio", app, app.ToLowerInvariant(), media, list);
            nodes[id] = n;
            foreach (var p in list) ports[p.Id] = p;
            return n;
        }

        var sinks = new List<PwNode>();
        for (var i = 0; i < Sinks; i++)
            sinks.Add(Sink(100 + i, $"Sink{i}", $"Sink {i}"));

        for (var i = 0; i < Captures; i++)
        {
            var id = 300 + i;
            var p = new PwPort(NextPort(), id, PortDirection.Output, "capture_MONO", "MONO", 0);
            nodes[id] = new PwNode(id, $"capture{i}", $"Capture {i}", "Audio/Source", null, null, null, new List<PwPort> { p });
            ports[p.Id] = p;
        }

        var linkId = 500_000 + generation * 10_000;
        for (var i = 0; i < Apps; i++)
        {
            if ((i + generation) % 3 == 0) continue; // a rotating third of apps is not running

            var appNode = App(10_000 + generation * 1_000 + i, $"App{i}", $"Media {i} g{generation}");

            // Half the running apps have a live link into their favourite sink; alternate managed/unowned.
            if (i % 2 == 0)
            {
                var sink = sinks[i % Sinks];
                var managed = i % 4 == 0;
                var props = managed
                    ? new Dictionary<string, string> { [PwLink.ManagedPropKey] = "true", [PwLink.RulePropKey] = $"rule-{i % 10}" }
                    : new Dictionary<string, string>();
                var outs = appNode.OutputPorts.ToList();
                var ins = sink.InputPorts.ToList();
                for (var c = 0; c < 2; c++)
                {
                    var lid = linkId++;
                    links[lid] = new PwLink(lid, appNode.Id, outs[c].Id, sink.Id, ins[c].Id, "active", props);
                }
            }
        }

        return new PwGraph(nodes, ports, links);
    }

    private static RulesDocument BigRules()
    {
        static MatchCriteria One(Field f, Op op, string v) => new(new[] { new Predicate(f, op, v) });

        var rules = new List<Rule>();
        for (var i = 0; i < 10; i++)
        {
            rules.Add(new Rule(
                Id: $"rule-{i}", Name: $"App{i} → Sink{i % Sinks}", Enabled: true,
                Source: One(Field.ApplicationName, Op.Equals, $"App{i}"),
                Target: One(Field.NodeName, Op.Equals, $"Sink{i % Sinks}")));
        }
        // Regex rules exercise the matcher's regex path on every node of every build.
        rules.Add(new Rule(
            Id: "rule-regex-1", Name: "App1x → Sink3", Enabled: true,
            Source: One(Field.ApplicationName, Op.Regex, @"^App1\d$"),
            Target: One(Field.NodeName, Op.Equals, "Sink3")));
        rules.Add(new Rule(
            Id: "rule-regex-2", Name: "App2x → Sink4", Enabled: true,
            Source: One(Field.ApplicationName, Op.Regex, @"^App2[0-4]$"),
            Target: One(Field.NodeName, Op.Equals, "Sink4")));

        var suppressions = new List<Suppression>
        {
            new("sup-1", One(Field.ApplicationName, Op.Equals, "App6"), One(Field.NodeName, Op.Equals, "Sink6")),
            new("sup-2", One(Field.ApplicationName, Op.Regex, @"^App3\d$"), One(Field.NodeName, Op.Equals, "Sink1")),
        };
        var @protected = new List<ProtectedMatch>
        {
            new("prot-1", One(Field.NodeName, Op.Equals, "Sink5")),
            new("prot-2", One(Field.ApplicationName, Op.Equals, "App7")),
        };

        return new RulesDocument(RulesDocument.CurrentVersion, rules, suppressions, @protected);
    }

    private sealed class NoopLinker : IPwLinker
    {
        public Task<LinkOpResult> ConnectAsync(int outPortId, int inPortId, string ruleId, CancellationToken ct = default)
            => Task.FromResult(LinkOpResult.Ok);
        public Task<LinkOpResult> DisconnectAsync(int linkId, CancellationToken ct = default)
            => Task.FromResult(LinkOpResult.Ok);
        public Task<LinkOpResult> DisconnectAsync(int outPortId, int inPortId, CancellationToken ct = default)
            => Task.FromResult(LinkOpResult.Ok);
    }
}
