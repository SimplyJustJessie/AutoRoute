using System.Text.Json.Serialization;

namespace AutoRoute.Engine.Model;

/// <summary>
/// A positive (keep-linked) rule: link every node matching <see cref="Source"/> to every node
/// matching <see cref="Target"/>, channel-paired, on every reconcile (ADR-0006). Auto-persisted
/// on every in-app edit (ADR-0009).
/// </summary>
public sealed record Rule(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("source")] MatchCriteria Source,
    [property: JsonPropertyName("target")] MatchCriteria Target);
