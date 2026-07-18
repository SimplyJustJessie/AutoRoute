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

        bool IsProtected(PwNode n) => rules.Protected.Any(pm => matcher.Matches(pm.Match, n));

        var columns = new List<ColumnModel>(targets.Count);
        foreach (var target in targets)
        {
            var columnProtected = IsProtected(target);
            // key = source identity → collapses an app's many streams into one card (app granularity).
            var cards = new Dictionary<string, CardBuild>();

            // --- Desired: managed cards from enabled positive Rules that target this column ---
            foreach (var rule in rules.Rules.Where(r => r.Enabled && matcher.Matches(r.Target, target)))
            {
                foreach (var src in matcher.Resolve(rule.Source, graph).Where(NodeRoles.IsAudioSource))
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
            foreach (var link in graph.Links.Where(l => l.InNodeId == target.Id))
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
                .Select(b => b.ToModel(columnProtected || IsProtected(b.Representative)))
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
                    Protected: IsProtected(rep));
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
