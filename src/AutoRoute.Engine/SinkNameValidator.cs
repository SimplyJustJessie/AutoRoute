using System.Text.RegularExpressions;

namespace AutoRoute.Engine;

/// <summary>
/// Validation shared by the create-sink UI and the legacy-conf importer. The name becomes both
/// PulseAudio's <c>sink_name</c> and the node's <c>node.name</c>, and is embedded verbatim in the
/// generated conf.d drop-in and pactl argv — so it is restricted to characters that can never break
/// module-arg or SPA-JSON quoting. Descriptions ride inside single quotes in
/// <c>sink_properties</c>, so a description must not contain quotes.
/// </summary>
public static partial class SinkNameValidator
{
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && NamePattern().IsMatch(name);

    public static bool IsValidDescription(string? description) =>
        !string.IsNullOrEmpty(description) && !description.Contains('"') && !description.Contains('\'');
}
