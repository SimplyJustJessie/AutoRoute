using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AutoRoute.Engine.Model;

/// <summary>
/// A stable-key matcher: a set of AND-ed <see cref="Predicate"/>s resolved to live nodes each
/// reconcile. Both a Rule's source and target are MatchCriteria (ADR-0006 symmetric rules);
/// a fixed sink is just the degenerate case of a single <c>NodeName Equals</c> predicate.
/// </summary>
public sealed record MatchCriteria(
    [property: JsonPropertyName("predicates")] IReadOnlyList<Predicate> Predicates)
{
    public static MatchCriteria Empty { get; } = new(Array.Empty<Predicate>());
}
