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

    public static string PwDumpSampleJson => File.ReadAllText(PwDumpSamplePath);
    public static string GateTaggedLinkJson => File.ReadAllText(GateTaggedLinkPath);
}
