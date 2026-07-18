using System;
using System.IO;

namespace AutoRoute.Tests.TestSupport;

/// <summary>Locates the real captured fixtures copied next to the test assembly.</summary>
public static class Fixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string PwDumpSamplePath => Path.Combine(Dir, "pw-dump.sample.json");
    public static string PwMonSamplePath => Path.Combine(Dir, "pw-mon.sample.txt");

    /// <summary>Real pw-dump slice of the ownership-gate link, as captured live (managed prop is a JSON boolean).</summary>
    public static string GateTaggedLinkPath => Path.Combine(Dir, "pw-dump.gate-tagged-link.json");

    /// <summary>
    /// `pactl list modules short` shape (index\tname\targs) with null-sink, tagged/untagged,
    /// no-sink_name, and junk rows. Re-capture live via scripts/v2-gate.sh (M1).
    /// </summary>
    public static string PactlModulesShortPath => Path.Combine(Dir, "pactl-modules.short.sample.txt");

    public static string PwDumpSampleJson => File.ReadAllText(PwDumpSamplePath);
    public static string GateTaggedLinkJson => File.ReadAllText(GateTaggedLinkPath);
}
