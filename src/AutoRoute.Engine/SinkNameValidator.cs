using System.Text.RegularExpressions;

namespace AutoRoute.Engine;

/// <summary>
/// Validation shared by the create-sink UI and the legacy-conf importer. The name becomes both
/// PulseAudio's <c>sink_name</c> and the node's <c>node.name</c>, and is embedded verbatim in the
/// generated conf.d drop-in and pactl argv — so it is restricted to characters that can never break
/// module-arg or SPA-JSON quoting. Descriptions ride inside single quotes in <c>sink_properties</c>,
/// which is itself the double-quoted value of the drop-in's <c>args</c> string, so a description
/// must contain neither quote style, nor a backslash (would collide with SPA-JSON's <c>\"</c>/<c>\\</c>
/// escaping), nor any control character (a newline breaks the single-line <c>args = "…"</c> value,
/// failing every declared sink at boot).
/// </summary>
public static partial class SinkNameValidator
{
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex NamePattern();

    // Any char except the two quote styles, a backslash, or a Unicode control character (\p{Cc}
    // covers newline, CR, tab, NUL, DEL and the C1 range).
    [GeneratedRegex(@"^[^""'\\\p{Cc}]+$")]
    private static partial Regex DescriptionPattern();

    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && NamePattern().IsMatch(name);

    public static bool IsValidDescription(string? description) =>
        !string.IsNullOrEmpty(description) && DescriptionPattern().IsMatch(description);
}
