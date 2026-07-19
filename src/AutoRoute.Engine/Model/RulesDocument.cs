using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AutoRoute.Engine.Model;

/// <summary>
/// The whole persisted routing policy — the on-disk shape of <c>~/.config/autoroute/rules.json</c>.
/// Serialized with the exact keys shown in PLAN.md ("version", "rules", "suppressions", "protected");
/// v2 (PLAN.v2.md / ADR-0011) adds "virtualSinks", the declared virtual-sink set.
/// </summary>
public sealed record RulesDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("rules")] IReadOnlyList<Rule> Rules,
    [property: JsonPropertyName("suppressions")] IReadOnlyList<Suppression> Suppressions,
    [property: JsonPropertyName("protected")] IReadOnlyList<ProtectedMatch> Protected,
    [property: JsonPropertyName("virtualSinks")] IReadOnlyList<VirtualSinkSpec> VirtualSinks)
{
    public const int CurrentVersion = 2;

    /// <summary>A fresh, empty policy at the current schema version.</summary>
    public static RulesDocument Empty { get; } = new(
        CurrentVersion,
        Array.Empty<Rule>(),
        Array.Empty<Suppression>(),
        Array.Empty<ProtectedMatch>(),
        Array.Empty<VirtualSinkSpec>());

    /// <summary>
    /// Upgrades any loaded document to the current schema in memory: a v1 file (no
    /// <c>virtualSinks</c>) deserializes with a null list, normalized here to empty, and the
    /// version is bumped so the next save writes v2. Idempotent for current documents.
    /// </summary>
    public RulesDocument Normalized()
    {
        var sinks = VirtualSinks ?? Array.Empty<VirtualSinkSpec>();
        if (Version >= CurrentVersion && ReferenceEquals(sinks, VirtualSinks)) return this;
        return this with
        {
            Version = Math.Max(Version, CurrentVersion),
            VirtualSinks = sinks,
        };
    }
}
