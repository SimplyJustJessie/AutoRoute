using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Engine;

/// <summary>
/// Evaluates <see cref="MatchCriteria"/> against live graph nodes. Predicates within a criteria
/// are AND-ed; each reads the stable-key node field named by <see cref="Field"/> and applies its
/// <see cref="Op"/> (Equals = exact ordinal, Contains = ordinal substring, Regex = compiled +
/// timeout-guarded). A null node field never matches (conservative — ADR-0003/0006).
/// App-granularity falls out naturally: matching <c>ApplicationName Equals Zen</c> resolves every
/// one of an app's streams, since they share the stable key.
/// </summary>
public sealed class RuleMatcher : IRuleMatcher
{
    /// <summary>Guards against catastrophic backtracking on a user-authored pattern.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    // Compiled regexes are reused across reconciles (patterns are stable strings from rules.json).
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);

    public bool Matches(MatchCriteria criteria, PwNode node)
    {
        if (criteria is null || node is null) return false;

        var predicates = criteria.Predicates;
        // An empty criteria matches nothing — a rule with no predicates must never link everything.
        if (predicates is null || predicates.Count == 0) return false;

        foreach (var predicate in predicates)
        {
            if (!MatchesPredicate(predicate, node)) return false; // AND
        }
        return true;
    }

    public IEnumerable<PwNode> Resolve(MatchCriteria criteria, PwGraph graph)
    {
        if (criteria is null || graph is null) yield break;

        foreach (var node in graph.Nodes)
        {
            if (Matches(criteria, node)) yield return node;
        }
    }

    private bool MatchesPredicate(Predicate predicate, PwNode node)
    {
        var field = FieldValue(predicate.Field, node);

        return predicate.Op switch
        {
            Op.Equals => field is not null && string.Equals(field, predicate.Value, StringComparison.Ordinal),
            Op.Contains => field is not null && field.Contains(predicate.Value, StringComparison.Ordinal),
            Op.Regex => MatchesRegex(predicate.Value, field),
            _ => false,
        };
    }

    private static string? FieldValue(Field field, PwNode node) => field switch
    {
        Field.ApplicationName => node.ApplicationName,
        Field.NodeName => node.NodeName,
        Field.ProcessBinary => node.ProcessBinary,
        Field.MediaName => node.MediaName,
        Field.MediaClass => node.MediaClass,
        _ => null,
    };

    private bool MatchesRegex(string pattern, string? field)
    {
        // Conservative: a missing field never matches, even against a permissive pattern like ".*".
        if (field is null) return false;

        var regex = GetRegex(pattern);
        if (regex is null) return false; // invalid pattern → treat as non-matching, never throw into the loop

        try
        {
            return regex.IsMatch(field);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private Regex? GetRegex(string pattern) => _regexCache.GetOrAdd(pattern, static p =>
    {
        try
        {
            return new Regex(p, RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return null; // malformed pattern — cached as null so we don't retry-compile every cycle
        }
    });
}
