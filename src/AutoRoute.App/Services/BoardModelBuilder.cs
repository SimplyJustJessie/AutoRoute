using System.Collections.Generic;
using System.Linq;
using AutoRoute.App.ViewModels;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Services;

/// <summary>
/// Pure function: (live graph + policy) → <see cref="BoardSnapshot"/>. No side effects, no VM
/// state — this is the testable heart the headless smoke check asserts against, and the input to
/// the ViewModel diff-merge. It unions the "desired" set (managed cards from Rules) with the
/// "actual" set (live links), keyed by Target column and Source identity, so a card appears the
/// instant a Rule exists and mirrors the whole live graph on first launch.
///
/// State precedence (ADR-0008): Protected &gt; Managed &gt; Unsaved/Manual.
/// </summary>
public static class BoardModelBuilder
{
    public static BoardSnapshot Build(
        PwGraph graph,
        RulesDocument rules,
        IRuleMatcher matcher,
        IReadOnlySet<string> keptManual,
        bool showMonitors)
    {
        var targets = graph.Nodes.Where(NodeRoles.IsTargetSink)
            .OrderBy(NodeRoles.TargetTitle, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Protected membership, resolved once per build — consulted per target, card and palette
        // entry below (was: a full rules.Protected × matcher scan at every call site).
        var protectedIds = new HashSet<int>();
        if (rules.Protected.Count > 0)
        {
            foreach (var node in graph.Nodes)
                if (rules.Protected.Any(pm => matcher.Matches(pm.Match, node)))
                    protectedIds.Add(node.Id);
        }

        // A rule's Source resolution is target-independent — resolve each enabled rule once,
        // not once per column (was: rules × columns × nodes matcher calls).
        var ruleSources = new List<(Rule Rule, List<PwNode> Sources)>();
        foreach (var rule in rules.Rules)
        {
            if (!rule.Enabled) continue;
            var sources = matcher.Resolve(rule.Source, graph).Where(NodeRoles.IsAudioSource).ToList();
            if (sources.Count > 0) ruleSources.Add((rule, sources));
        }

        // Live links indexed by target node in one pass (was: an O(links) scan per column).
        var linksByTarget = new Dictionary<int, List<PwLink>>();
        foreach (var link in graph.Links)
        {
            if (!linksByTarget.TryGetValue(link.InNodeId, out var list))
                linksByTarget[link.InNodeId] = list = new List<PwLink>();
            list.Add(link);
        }

        var columns = new List<ColumnModel>(targets.Count);
        foreach (var target in targets)
        {
            var columnProtected = protectedIds.Contains(target.Id);
            // key = source identity → collapses an app's many streams into one card (app granularity).
            var cards = new Dictionary<string, CardBuild>();

            // --- Desired: managed cards from enabled positive Rules that target this column ---
            foreach (var (rule, sources) in ruleSources)
            {
                if (!matcher.Matches(rule.Target, target)) continue;
                foreach (var src in sources)
                {
                    var key = NodeRoles.SourceIdentity(src);
                    if (!cards.TryGetValue(key, out var b))
                    {
                        b = new CardBuild(src);
                        cards[key] = b;
                    }
                    b.AddNode(src);
                    b.State = CardState.Managed;
                    b.RuleId ??= rule.Id;
                    b.RuleName ??= rule.Name;
                }
            }

            // --- Actual: live links feeding this target ---
            foreach (var link in linksByTarget.TryGetValue(target.Id, out var targetLinks)
                         ? targetLinks : (IReadOnlyList<PwLink>)System.Array.Empty<PwLink>())
            {
                var src = graph.Node(link.OutNodeId);
                if (src is null || !NodeRoles.IsAudioSource(src)) continue;

                var key = NodeRoles.SourceIdentity(src);
                if (cards.TryGetValue(key, out var existing))
                {
                    existing.AddNode(src);
                    existing.LinkId ??= link.Id;
                    if (link.IsManaged)
                    {
                        existing.State = CardState.Managed;
                        existing.RuleId ??= link.RuleId;
                    }
                    continue;
                }

                var b = new CardBuild(src);
                b.AddNode(src);
                b.LinkId = link.Id;
                if (link.IsManaged)
                {
                    b.State = CardState.Managed;
                    b.RuleId = link.RuleId;
                }
                else
                {
                    var kept = keptManual.Contains(CardKey(target.Id, key));
                    b.State = kept ? CardState.Manual : CardState.Unsaved;
                }
                cards[key] = b;
            }

            var cardModels = cards.Values
                .Select(b => b.ToModel(columnProtected || protectedIds.Contains(b.Representative.Id)))
                .OrderBy(c => c.Title, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            columns.Add(new ColumnModel(
                TargetNodeId: target.Id,
                Key: NodeRoles.TargetIdentity(target),
                Title: NodeRoles.TargetTitle(target),
                Subtitle: NodeRoles.TargetSubtitle(target),
                Protected: columnProtected,
                Cards: cardModels));
        }

        // --- Palette: every audio Source, app-granularity, monitors optional ---
        var palette = graph.Nodes.Where(NodeRoles.IsAudioSource)
            .GroupBy(NodeRoles.SourceIdentity)
            .Select(g =>
            {
                var rep = g.First();
                var monitor = g.All(NodeRoles.IsMonitorSink);
                return new PaletteItemModel(
                    Key: g.Key,
                    RepresentativeNodeId: rep.Id,
                    AllNodeIds: g.Select(n => n.Id).ToList(),
                    Title: NodeRoles.SourceTitle(rep),
                    Subtitle: NodeRoles.SourceSubtitle(rep),
                    Kind: NodeRoles.KindOf(rep),
                    IsMonitor: monitor,
                    Protected: protectedIds.Contains(rep.Id));
            })
            .Where(p => showMonitors || !p.IsMonitor)
            .OrderBy(p => p.IsMonitor)
            .ThenBy(p => p.Title, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BoardSnapshot(columns, palette);
    }

    /// <summary>The keptManual set key: a card is per (target, source identity).</summary>
    public static string CardKey(int targetNodeId, string sourceIdentity) => targetNodeId + "|" + sourceIdentity;

    private sealed class CardBuild
    {
        private readonly List<int> _nodeIds = new();
        public PwNode Representative { get; }
        public CardState State { get; set; } = CardState.Unsaved;
        public string? RuleId { get; set; }
        public string? RuleName { get; set; }
        public int? LinkId { get; set; }

        public CardBuild(PwNode representative) => Representative = representative;

        public void AddNode(PwNode n)
        {
            if (!_nodeIds.Contains(n.Id)) _nodeIds.Add(n.Id);
        }

        public CardModel ToModel(bool isProtected)
        {
            var state = isProtected ? CardState.Protected : State;
            var identity = NodeRoles.SourceIdentity(Representative);
            var tooltip = state switch
            {
                CardState.Managed => RuleName is { Length: > 0 } ? $"Managed Link — rule: {RuleName}" : "Managed Link",
                CardState.Unsaved => "Unsaved external Link — Save to keep it after relaunch",
                CardState.Manual => "Manual Link — not reproduced automatically",
                CardState.Protected => "Protected — AutoRoute will not touch this node",
                _ => string.Empty,
            };
            return new CardModel(
                Key: identity,
                RepresentativeNodeId: Representative.Id,
                SourceIdentity: identity,
                AllSourceNodeIds: _nodeIds.Count > 0 ? _nodeIds.ToList() : new List<int> { Representative.Id },
                Title: NodeRoles.SourceTitle(Representative),
                Subtitle: NodeRoles.SourceSubtitle(Representative),
                State: state,
                RuleId: RuleId,
                RuleName: RuleName,
                LinkId: LinkId,
                Tooltip: tooltip);
        }
    }
}
