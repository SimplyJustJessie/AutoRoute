using System;
using System.IO;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Design;

/// <summary>
/// Locates and parses the captured <c>pw-dump.sample.json</c> fixture for standalone UI dev. Walks
/// up from the running assembly to find the tests fixture in the repo; if it can't (e.g. shipped
/// build), falls back to the hand-built <see cref="DesignGraph"/> so the app never fails to render.
/// </summary>
public static class FixtureLocator
{
    private const string RelativeFixture = "tests/AutoRoute.Tests/fixtures/pw-dump.sample.json";

    /// <summary>The real captured graph if the fixture is found, else the in-memory design graph.</summary>
    public static PwGraph LoadGraphOrDesign()
    {
        var path = TryFindFixture();
        if (path is not null)
        {
            try { return PwDumpReader.Parse(File.ReadAllText(path)); }
            catch { /* fall through to the deterministic design graph */ }
        }
        return DesignGraph.Build();
    }

    /// <summary>Parse a graph from an explicit fixture path (used by the smoke test).</summary>
    public static PwGraph LoadFrom(string path) => PwDumpReader.Parse(File.ReadAllText(path));

    /// <summary>Best-effort search for the repo fixture, walking up from the assembly location.</summary>
    public static string? TryFindFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, RelativeFixture);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
