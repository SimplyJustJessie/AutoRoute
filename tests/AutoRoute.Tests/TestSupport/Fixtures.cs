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
    /// Real `pactl list modules short` output captured by scripts/v2-gate.sh (v2 M1, 11/11 pass):
    /// libpipewire modules with multi-line args, the user's four legacy null sinks (unquoted
    /// sink_properties, untagged), and the tagged gate sink.
    /// </summary>
    public static string PactlModulesShortPath => Path.Combine(Dir, "pactl-modules.short.sample.txt");

    /// <summary>
    /// Real pw-dump slice of the gate's pactl-created null sink (v2 M1): Audio/Sink node whose
    /// props carry <c>autoroute.managed</c> as a JSON boolean.
    /// </summary>
    public static string PwDumpManagedSinkPath => Path.Combine(Dir, "pw-dump.managed-sink.json");

    public static string PwDumpSampleJson => File.ReadAllText(PwDumpSamplePath);
    public static string GateTaggedLinkJson => File.ReadAllText(GateTaggedLinkPath);
}
