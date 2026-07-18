using System.Text.Json.Serialization;

namespace AutoRoute.Engine.Model;

/// <summary>
/// A negative (keep-UN-linked) rule: the same symmetric <c>source → target</c> pair, but the
/// reconciler deletes any matching link every cycle — including unowned links (the one case it
/// deletes something it did not create), unless an endpoint is Protected (ADR-0007 / ADR-0008).
/// </summary>
public sealed record Suppression(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] MatchCriteria Source,
    [property: JsonPropertyName("target")] MatchCriteria Target);
