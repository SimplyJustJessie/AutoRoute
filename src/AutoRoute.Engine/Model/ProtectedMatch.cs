using System.Text.Json.Serialization;

namespace AutoRoute.Engine.Model;

/// <summary>
/// A "do not touch" marker: AutoRoute never creates, deletes, or suppresses any link touching a
/// node matching <see cref="Match"/>. Overrides all Rules and Suppressions
/// (precedence Protected &gt; Suppression &gt; Positive, ADR-0008).
/// </summary>
public sealed record ProtectedMatch(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("match")] MatchCriteria Match);
