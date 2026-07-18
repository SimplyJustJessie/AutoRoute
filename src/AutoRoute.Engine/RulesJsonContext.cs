using System.Text.Json.Serialization;
using AutoRoute.Engine.Model;

namespace AutoRoute.Engine;

/// <summary>
/// Source-generated (reflection-free) serialization for <see cref="RulesDocument"/>. The on-disk
/// key names are frozen by the <c>[JsonPropertyName]</c> attributes on the model records
/// (<c>version/rules/suppressions/protected</c>, predicate <c>field/op/value</c>); the
/// <see cref="Field"/>/<see cref="Op"/> enums serialize by name via their own
/// <c>[JsonConverter(JsonStringEnumConverter&lt;T&gt;)]</c> attributes. Indented so a user can
/// hand-edit <c>~/.config/autoroute/rules.json</c>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RulesDocument))]
internal sealed partial class RulesJsonContext : JsonSerializerContext
{
}
