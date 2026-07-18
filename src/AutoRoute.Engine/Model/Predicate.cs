using System.Text.Json.Serialization;

namespace AutoRoute.Engine.Model;

/// <summary>The stable-key node field a <see cref="Predicate"/> tests. Serialized by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Field>))]
public enum Field
{
    ApplicationName,
    NodeName,
    ProcessBinary,
    MediaName,
    MediaClass,
}

/// <summary>How a <see cref="Predicate"/> compares its field to its value. Serialized by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Op>))]
public enum Op
{
    Equals,
    Contains,
    Regex,
}

/// <summary>
/// One stable-key test, e.g. <c>{ "field": "ApplicationName", "op": "Equals", "value": "Zen" }</c>.
/// Predicates within a <see cref="MatchCriteria"/> are AND-ed.
/// </summary>
public sealed record Predicate(
    [property: JsonPropertyName("field")] Field Field,
    [property: JsonPropertyName("op")] Op Op,
    [property: JsonPropertyName("value")] string Value);
