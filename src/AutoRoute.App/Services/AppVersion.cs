using System;
using System.Reflection;

namespace AutoRoute.App.Services;

/// <summary>
/// The running app's own version, baked in at publish time via <c>-p:InformationalVersion</c> (see
/// <c>scripts/build-appimage.sh</c>) and read back here so the updater has something to compare
/// against the latest Gitea release tag. An unbaked local build (a plain <c>dotnet run</c>) reports
/// <see cref="Dev"/>, which never compares as older — so a dev build never offers to "update" itself
/// down onto a released tag.
/// </summary>
public sealed class AppVersion
{
    /// <summary>What <see cref="Current"/> reads for an unbaked build.</summary>
    public const string Dev = "dev";

    /// <summary>The running app's version, e.g. <c>0.3.0</c>, or <see cref="Dev"/> when not baked in.</summary>
    public string Current { get; }

    public AppVersion() : this(ReadInformationalVersion()) { }

    /// <summary>Test seam: construct from a raw informational-version string.</summary>
    public AppVersion(string? informationalVersion)
    {
        Current = Normalize(informationalVersion);
    }

    private static string? ReadInformationalVersion() =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>Strip the <c>+&lt;git-sha&gt;</c> suffix .NET appends and any leading <c>v</c>; blank ⇒ <see cref="Dev"/>.</summary>
    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Dev;
        var s = raw.Trim();
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        s = s.TrimStart('v', 'V');
        return s.Length == 0 ? Dev : s;
    }

    /// <summary>
    /// Parse a release tag (<c>v0.3.0</c>, <c>0.3.0</c>) down to its numeric <c>X.Y.Z</c> core.
    /// Returns <c>false</c> for anything without one (e.g. <see cref="Dev"/>).
    /// </summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var s = tag.Trim().TrimStart('v', 'V');
        // Keep only the leading dotted-numeric core, dropping any "-rc1" / "+meta" suffix.
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end];

        // Version.TryParse needs at least "major.minor"; our tags are always X.Y.Z.
        if (!Version.TryParse(s, out var parsed)) return false;
        version = parsed;
        return true;
    }

    /// <summary>True when <paramref name="candidateTag"/> is a strictly newer release than <see cref="Current"/>.</summary>
    public bool IsNewer(string? candidateTag)
    {
        // A dev/unknown current version never parses, so it never self-updates (avoids a dev build
        // downgrading itself onto a release, and avoids churn when the version isn't baked in).
        if (!TryParseTag(Current, out var mine)) return false;
        if (!TryParseTag(candidateTag, out var theirs)) return false;
        return theirs > mine;
    }
}
