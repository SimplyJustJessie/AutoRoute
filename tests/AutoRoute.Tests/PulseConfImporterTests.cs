using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

public sealed class PulseConfImporterTests : IDisposable
{
    private readonly string _confD;
    private readonly string _rulesPath;

    public PulseConfImporterTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "autoroute-tests", Guid.NewGuid().ToString("N"));
        _confD = Path.Combine(root, "pipewire-pulse.conf.d");
        Directory.CreateDirectory(_confD);
        _rulesPath = Path.Combine(root, "rules.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_confD)!, recursive: true); } catch { /* best-effort */ }
    }

    private PulseConfImporter NewImporter() => new(_confD, SinkDropInWriter.FileName);

    [Fact]
    public void Parse_recovers_the_users_legacy_file_shape()
    {
        var sinks = PulseConfImporter.Parse(File.ReadAllText(
            Path.Combine(Fixtures.Dir, "virtual-sinks.conf.sample")));

        // The four null sinks — the loopback module is not a sink and is ignored.
        Assert.Equal(new[] { "MusicSink", "DiscordSink", "GameSink", "DesktopSink" },
            sinks.Select(s => s.Name).ToArray());
        // Bare sink_properties=device.description='…' (the real legacy shape, per the live gate
        // capture): the space-containing description must survive whole, not truncate at the space.
        Assert.Equal(new[] { "Music Sink", "Discord Sink", "Game Sink", "Desktop Sink" },
            sinks.Select(s => s.Description).ToArray());
        Assert.All(sinks, s => Assert.False(s.Mono));
    }

    [Fact]
    public void Parse_recovers_our_own_dropin_shape_too()
    {
        // The double-quoted sink_properties form the generated drop-in writes.
        var sinks = PulseConfImporter.Parse(SinkDropInWriter.Generate(new[]
        {
            new VirtualSinkSpec("x", "StreamSink", "Stream Sink", SinkChannels.Stereo),
        }));

        var sink = Assert.Single(sinks);
        Assert.Equal("StreamSink", sink.Name);
        Assert.Equal("Stream Sink", sink.Description);
    }

    [Fact]
    public void Parse_handles_mono_unquoted_description_and_junk()
    {
        const string conf = """
        pulse.cmd = [
            { cmd = "load-module" args = "module-null-sink sink_name=Narration channels=1 channel_map=mono sink_properties=device.description=Narration" }
            { cmd = "load-module" args = "module-null-sink" }
            this line is garbage {{{
            { cmd = "load-module" args = "module-echo-cancel source_name=ec" }
        ]
        """;
        var sinks = PulseConfImporter.Parse(conf);

        // The nameless null sink and the non-null-sink module are skipped; junk doesn't throw.
        var sink = Assert.Single(sinks);
        Assert.Equal("Narration", sink.Name);
        Assert.Equal("Narration", sink.Description);
        Assert.True(sink.Mono);
    }

    [Fact]
    public async Task ImportAsync_appends_undeclared_sinks_and_reports_the_legacy_file()
    {
        File.Copy(Path.Combine(Fixtures.Dir, "virtual-sinks.conf.sample"),
            Path.Combine(_confD, "virtual-sinks.conf"));

        using var store = new RuleStore(_rulesPath);
        await store.LoadAsync();
        // MusicSink is already declared — import must not duplicate it.
        await store.SaveAsync(RulesDocument.Empty with
        {
            VirtualSinks = new[] { new VirtualSinkSpec("pre", "MusicSink", "Music Sink", SinkChannels.Stereo) },
        });

        var result = await NewImporter().ImportAsync(store);

        Assert.Equal(new[] { "DiscordSink", "GameSink", "DesktopSink" },
            result.Imported.Select(s => s.Name).ToArray());
        Assert.Equal(new[] { Path.Combine(_confD, "virtual-sinks.conf") }, result.LegacyFilesStillPresent);
        Assert.Equal(4, store.Current.VirtualSinks.Count);
    }

    [Fact]
    public async Task ImportAsync_is_idempotent()
    {
        File.Copy(Path.Combine(Fixtures.Dir, "virtual-sinks.conf.sample"),
            Path.Combine(_confD, "virtual-sinks.conf"));

        using var store = new RuleStore(_rulesPath);
        await store.LoadAsync();

        var first = await NewImporter().ImportAsync(store);
        var second = await NewImporter().ImportAsync(store);

        Assert.Equal(4, first.Imported.Count);
        Assert.Empty(second.Imported); // everything already declared
        Assert.Single(second.LegacyFilesStillPresent); // …but the file still warrants the warning
        Assert.Equal(4, store.Current.VirtualSinks.Count);
    }

    [Fact]
    public async Task ImportAsync_skips_our_own_dropin_and_empty_dir_is_clean()
    {
        // Only AutoRoute's own generated file present — nothing to import, nothing legacy.
        await File.WriteAllTextAsync(Path.Combine(_confD, SinkDropInWriter.FileName),
            SinkDropInWriter.Generate(new[] { new VirtualSinkSpec("x", "GameSink", "Game Sink", SinkChannels.Stereo) }));

        using var store = new RuleStore(_rulesPath);
        await store.LoadAsync();

        var result = await NewImporter().ImportAsync(store);

        Assert.Empty(result.Imported);
        Assert.Empty(result.LegacyFilesStillPresent);
    }

    [Fact]
    public async Task ScanExternalSinkNames_reports_legacy_names_excluding_our_own_dropin()
    {
        File.Copy(Path.Combine(Fixtures.Dir, "virtual-sinks.conf.sample"),
            Path.Combine(_confD, "virtual-sinks.conf"));
        await File.WriteAllTextAsync(Path.Combine(_confD, SinkDropInWriter.FileName),
            SinkDropInWriter.Generate(new[] { new VirtualSinkSpec("x", "StreamSink", "Stream Sink", SinkChannels.Stereo) }));

        var names = NewImporter().ScanExternalSinkNames();

        // The legacy file's sinks are external; our own drop-in's StreamSink is NOT.
        Assert.Equal(new[] { "DesktopSink", "DiscordSink", "GameSink", "MusicSink" },
            names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ImportAsync_missing_directory_returns_empty()
    {
        Directory.Delete(_confD, recursive: true);
        using var store = new RuleStore(_rulesPath);
        await store.LoadAsync();

        var result = await NewImporter().ImportAsync(store);

        Assert.Empty(result.Imported);
        Assert.Empty(result.LegacyFilesStillPresent);
    }
}
