using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AutoRoute.Engine.Model;

/// <summary>
/// The whole persisted routing policy — the on-disk shape of <c>~/.config/autoroute/rules.json</c>.
/// Serialized with the exact keys shown in PLAN.md ("version", "rules", "suppressions", "protected").
/// </summary>
public sealed record RulesDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("rules")] IReadOnlyList<Rule> Rules,
    [property: JsonPropertyName("suppressions")] IReadOnlyList<Suppression> Suppressions,
    [property: JsonPropertyName("protected")] IReadOnlyList<ProtectedMatch> Protected)
{
    public const int CurrentVersion = 1;

    /// <summary>A fresh, empty policy at the current schema version.</summary>
    public static RulesDocument Empty { get; } = new(
        CurrentVersion,
        Array.Empty<Rule>(),
        Array.Empty<Suppression>(),
        Array.Empty<ProtectedMatch>());
}
