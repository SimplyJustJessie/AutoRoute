using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.Engine;

/// <summary>
/// The idempotent automation core. On every reconcile it drives the live graph toward the policy:
/// build the desired managed-link set from enabled positive rules, create what is missing, delete
/// stale managed links, enforce suppressions (deleting even unowned links), and never touch any
/// other unowned link. Precedence <b>Protected &gt; Suppression &gt; positive Rule</b> (ADR-0008):
/// a node matching a Protected marker is never linked, unlinked, or suppressed; a pair an active
/// Suppression covers is never added to the desired set, so a hand-edited rules.json that names both
/// a positive rule and a Suppression for the same pair stays stable instead of create-then-deleting.
///
/// <para>Every op is wrapped in try/catch; a transient failure (a port vanished mid-cycle) is logged
/// and self-heals on the next snapshot. A second reconcile over an unchanged graph issues zero ops.</para>
/// </summary>
public sealed class Reconciler : IReconciler
{
    private readonly IPwLinker _linker;
    private readonly IRuleMatcher _matcher;
    private readonly ILogger _log;

    public Reconciler(IPwLinker linker, IRuleMatcher matcher, ILogger<Reconciler>? log = null)
    {
        _linker = linker;
        _matcher = matcher;
        _log = log ?? NullLogger<Reconciler>.Instance;
    }

    public async Task ReconcileAsync(PwGraph graph, RulesDocument rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(rules);

        // Precompute Protected node ids once — a Protected endpoint overrides every rule/suppression.
        var protectedNodeIds = ProtectedNodeIds(graph, rules);

        // Resolve every active Suppression to node-id sets once, reused by both the desired-set guard
        // (Suppression &gt; Positive) and step 5 (deletion). Empty-resolving suppressions are dropped.
        var suppressions = ResolveSuppressions(graph, rules);

        // ---- Step 1: desired managed-link set D. Skip a pair whose source OR target is Protected,
        // ---- and skip a pair an active Suppression covers (Protected &gt; Suppression &gt; Positive):
        // ---- coexistence is out-of-contract (the UI enforces latest-action-wins), but a hand-edited
        // ---- rules.json naming both must not create-then-delete every cycle. The pair is simply never
        // ---- created; step 5 still deletes any link already present → stable, no flap.
        var desired = BuildDesired(graph, rules, protectedNodeIds, suppressions);

        // ---- Step 2: actual set A, split managed vs unowned (via PwLink.IsManaged) ---------------
        // (graph.ManagedLinks / graph.UnownedLinks already partition this for us.)
        // Existing port pairs indexed in one pass — replaces a per-desired-pair O(links) scan.
        var existingPairs = new HashSet<PortPairKey>(graph.Links.Count);
        foreach (var link in graph.Links)
            existingPairs.Add(new PortPairKey(link.OutPortId, link.InPortId));

        // Track link ids we've already deleted so steps 4 & 5 never double-issue.
        var disconnected = new HashSet<int>();

        // ---- Step 3: create D \ A (skip existing ports → no dupes; both ports must exist) --------
        foreach (var (pair, ruleId) in desired)
        {
            ct.ThrowIfCancellationRequested();

            if (existingPairs.Contains(pair))
                continue; // a link with these ports already exists (managed or unowned) — no duplicate

            // Guard: node-appeared-before-ports race — only connect when BOTH ports are in the snapshot.
            if (!graph.PortsById.ContainsKey(pair.OutPort) || !graph.PortsById.ContainsKey(pair.InPort))
                continue; // defer to the next snapshot

            await TryConnect(pair.OutPort, pair.InPort, ruleId, ct).ConfigureAwait(false);
        }

        // ---- Step 4: stale cleanup — managed links no longer in D -------------------------------
        foreach (var link in graph.ManagedLinks)
        {
            ct.ThrowIfCancellationRequested();

            if (desired.ContainsKey(new PortPairKey(link.OutPortId, link.InPortId)))
                continue; // still desired — keep it

            await TryDisconnect(link, disconnected, ct).ConfigureAwait(false);
        }

        // ---- Step 5: suppressions — delete matching links (managed OR unowned) -------------------
        // ---- unless either endpoint is Protected. Step 6 (never touch other unowned) is implicit.
        foreach (var (sourceIds, targetIds) in suppressions)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var link in graph.Links)
            {
                if (!sourceIds.Contains(link.OutNodeId) || !targetIds.Contains(link.InNodeId))
                    continue;

                // Protected wins: never delete a link touching a Protected node.
                if (protectedNodeIds.Contains(link.OutNodeId) || protectedNodeIds.Contains(link.InNodeId))
                    continue;

                await TryDisconnect(link, disconnected, ct).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------------------------

    private readonly record struct PortPairKey(int OutPort, int InPort);

    /// <summary>An active Suppression resolved to the node ids it currently matches on each side.</summary>
    private readonly record struct ResolvedSuppression(HashSet<int> SourceIds, HashSet<int> TargetIds);

    private Dictionary<PortPairKey, string> BuildDesired(
        PwGraph graph, RulesDocument rules, HashSet<int> protectedNodeIds,
        IReadOnlyList<ResolvedSuppression> suppressions)
    {
        var desired = new Dictionary<PortPairKey, string>();

        foreach (var rule in rules.Rules)
        {
            if (!rule.Enabled) continue;

            // Resolve both sides once, dropping Protected endpoints up front.
            var sources = ResolveUnprotected(rule.Source, graph, protectedNodeIds);
            if (sources.Count == 0) continue;
            var targets = ResolveUnprotected(rule.Target, graph, protectedNodeIds);
            if (targets.Count == 0) continue;

            foreach (var source in sources)
            {
                foreach (var target in targets)
                {
                    // Suppression &gt; Positive: never build a pair an active Suppression covers, so a
                    // coexisting positive+suppression (hand edit) can't flap. Same node-level matching
                    // used for Protected exclusion above.
                    if (IsSuppressedPair(source.Id, target.Id, suppressions)) continue;

                    var pairing = ChannelMapper.Map(source, target);
                    foreach (var pp in pairing.Pairs)
                    {
                        var key = new PortPairKey(pp.OutPortId, pp.InPortId);
                        // First rule to claim a port pair tags it; duplicates collapse to one link.
                        if (!desired.ContainsKey(key)) desired[key] = rule.Id;
                    }
                }
            }
        }
        return desired;
    }

    private static bool IsSuppressedPair(
        int sourceNodeId, int targetNodeId, IReadOnlyList<ResolvedSuppression> suppressions)
    {
        foreach (var s in suppressions)
        {
            if (s.SourceIds.Contains(sourceNodeId) && s.TargetIds.Contains(targetNodeId))
                return true;
        }
        return false;
    }

    private List<ResolvedSuppression> ResolveSuppressions(PwGraph graph, RulesDocument rules)
    {
        var resolved = new List<ResolvedSuppression>();
        foreach (var suppression in rules.Suppressions)
        {
            var sourceIds = ResolveIds(suppression.Source, graph);
            if (sourceIds.Count == 0) continue; // can't match any link/pair this cycle
            var targetIds = ResolveIds(suppression.Target, graph);
            if (targetIds.Count == 0) continue;
            resolved.Add(new ResolvedSuppression(sourceIds, targetIds));
        }
        return resolved;
    }

    // Resolve criteria to live nodes, dropping any that match a Protected marker.
    private List<PwNode> ResolveUnprotected(MatchCriteria criteria, PwGraph graph, HashSet<int> protectedNodeIds)
    {
        var nodes = new List<PwNode>();
        foreach (var node in _matcher.Resolve(criteria, graph))
        {
            if (protectedNodeIds.Contains(node.Id)) continue;
            nodes.Add(node);
        }
        return nodes;
    }

    private HashSet<int> ProtectedNodeIds(PwGraph graph, RulesDocument rules)
    {
        var ids = new HashSet<int>();
        if (rules.Protected.Count == 0) return ids;

        foreach (var node in graph.Nodes)
        {
            foreach (var marker in rules.Protected)
            {
                if (_matcher.Matches(marker.Match, node))
                {
                    ids.Add(node.Id);
                    break;
                }
            }
        }
        return ids;
    }

    private HashSet<int> ResolveIds(MatchCriteria criteria, PwGraph graph)
    {
        var ids = new HashSet<int>();
        foreach (var node in _matcher.Resolve(criteria, graph))
            ids.Add(node.Id);
        return ids;
    }

    private async Task TryConnect(int outPort, int inPort, string ruleId, CancellationToken ct)
    {
        try
        {
            var result = await _linker.ConnectAsync(outPort, inPort, ruleId, ct).ConfigureAwait(false);
            if (!result.Success)
                _log.LogDebug("connect {Out}->{In} ({Rule}) failed: {Error}; will retry next snapshot",
                    outPort, inPort, ruleId, result.Error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "connect {Out}->{In} ({Rule}) threw; self-heals next snapshot",
                outPort, inPort, ruleId);
        }
    }

    private async Task TryDisconnect(PwLink link, HashSet<int> disconnected, CancellationToken ct)
    {
        if (!disconnected.Add(link.Id)) return; // already issued this cycle

        try
        {
            var result = await _linker.DisconnectAsync(link.Id, ct).ConfigureAwait(false);
            if (!result.Success)
                _log.LogDebug("disconnect link {Id} failed: {Error}; will retry next snapshot",
                    link.Id, result.Error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "disconnect link {Id} threw; self-heals next snapshot", link.Id);
        }
    }
}
