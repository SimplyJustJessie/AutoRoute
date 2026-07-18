using System.Collections.Generic;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Engine;

/// <summary>
/// Resolves <see cref="MatchCriteria"/> against live graph nodes (AND-ed predicates,
/// app-granularity, ADR-0003/0006). Wave 2 (Engine) implements the behavior.
/// </summary>
public interface IRuleMatcher
{
    /// <summary>Does <paramref name="node"/> satisfy every predicate in <paramref name="criteria"/>?</summary>
    bool Matches(MatchCriteria criteria, PwNode node);

    /// <summary>Every node in <paramref name="graph"/> that satisfies <paramref name="criteria"/>.</summary>
    IEnumerable<PwNode> Resolve(MatchCriteria criteria, PwGraph graph);
}
