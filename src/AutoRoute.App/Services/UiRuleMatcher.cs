using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Services;

/// <summary>
/// UI-side implementation of the frozen <see cref="IRuleMatcher"/> so the board can resolve
/// Rules/Suppressions/Protected to live nodes for display — the Engine's real matcher ships a
/// stub in Wave 1. Semantics follow PLAN/CONTEXT exactly: AND-ed predicates over the five stable
/// keys, ordinal-insensitive Equals/Contains, and Regex. Wave 3 may swap in the Engine's
/// <c>RuleMatcher</c> for the same interface with no board changes.
/// </summary>
public sealed class UiRuleMatcher : IRuleMatcher
{
    private const StringComparison Ord = StringComparison.OrdinalIgnoreCase;

    /// <summary>Guards against catastrophic backtracking on a user-authored pattern.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    // Compiled patterns are reused across board rebuilds (patterns are stable strings from
    // rules.json); a malformed pattern caches as null so it isn't re-compiled every build.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex?> _regexCache =
        new(StringComparer.Ordinal);

    public bool Matches(MatchCriteria criteria, PwNode node)
    {
        // An empty criteria matches nothing (never "match every node") — safe for display + actions.
        if (criteria is null || criteria.Predicates.Count == 0) return false;
        return criteria.Predicates.All(p => MatchPredicate(p, node));
    }

    public IEnumerable<PwNode> Resolve(MatchCriteria criteria, PwGraph graph) =>
        graph.Nodes.Where(n => Matches(criteria, n));

    private bool MatchPredicate(Predicate p, PwNode n)
    {
        var value = FieldValue(p.Field, n);
        if (value is null) return false;

        return p.Op switch
        {
            Op.Equals => string.Equals(value, p.Value, Ord),
            Op.Contains => value.Contains(p.Value ?? string.Empty, Ord),
            Op.Regex => SafeRegex(value, p.Value),
            _ => false,
        };
    }

    private static string? FieldValue(Field f, PwNode n) => f switch
    {
        Field.ApplicationName => n.ApplicationName,
        Field.NodeName => n.NodeName,
        Field.ProcessBinary => n.ProcessBinary,
        Field.MediaName => n.MediaName,
        Field.MediaClass => n.MediaClass,
        _ => null,
    };

    private bool SafeRegex(string input, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        var regex = _regexCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    RegexTimeout);
            }
            catch (ArgumentException)
            {
                return null; // malformed user regex → no match, never throw into the UI
            }
        });
        if (regex is null) return false;

        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
