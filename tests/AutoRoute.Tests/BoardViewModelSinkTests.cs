using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.App.Design;
using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

/// <summary>Sink-management flows on the board VM: opt-in legacy import and tag-scoped deletion.</summary>
public sealed class BoardViewModelSinkTests : IDisposable
{
    private readonly string _confD;

    public BoardViewModelSinkTests()
    {
        _confD = Path.Combine(Path.GetTempPath(), "autoroute-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_confD);
    }

    public void Dispose()
    {
        try { Directory.Delete(_confD, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Import_button_is_the_only_path_that_writes_legacy_sinks()
    {
        File.Copy(Path.Combine(Fixtures.Dir, "virtual-sinks.conf.sample"),
            Path.Combine(_confD, "virtual-sinks.conf"));
        var importer = new PulseConfImporter(_confD, SinkDropInWriter.FileName);
        var notices = new AppNotices();
        var store = new MockRuleStore();

        var board = new BoardViewModel(
            new MockPwGraphService(PwGraph.Empty), new RecordingPwLinker(), store,
            new MockReconciler(), new UiRuleMatcher(), new MockSinkController(),
            notices, log: null, importer);
        await board.InitializeAsync();

        // Worker-style detection published; nothing imported yet.
        var detection = await importer.DetectAsync(store);
        notices.SetLegacyState(detection.Files, detection.Pending.Select(s => s.Name).ToList());

        Assert.True(board.HasPendingLegacyImport);
        Assert.Contains("not managed by AutoRoute", board.LegacyNoticeText);
        Assert.Empty(store.Current.VirtualSinks); // detect + offer: no write

        await board.ImportLegacySinksCommand.ExecuteAsync(null);

        Assert.Equal(4, store.Current.VirtualSinks.Count); // the click is the write
        Assert.False(board.HasPendingLegacyImport);
        Assert.Contains("remove the file", board.LegacyNoticeText); // retire warning remains
    }

    [Fact]
    public async Task DeleteSinkAsync_unloads_only_tagged_modules()
    {
        // GameSink was imported from a legacy conf that's still present: the live module is the
        // user's UNTAGGED one, plus a stale tagged twin. Un-declaring must only kill ours.
        var controller = new FakeSinkController();
        controller.Modules.Add(new NullSinkModule(7, "GameSink",
            "sink_name=GameSink sink_properties=device.description='Game Sink'"));
        controller.Modules.Add(new NullSinkModule(9, "GameSink",
            "sink_name=GameSink sink_properties=\"device.description='Game Sink' autoroute.managed=true\""));

        var store = new MockRuleStore(RulesDocument.Empty with
        {
            VirtualSinks = new[] { new VirtualSinkSpec("g", "GameSink", "Game Sink", SinkChannels.Stereo) },
        });
        var board = new BoardViewModel(
            new MockPwGraphService(GraphMutations.WithNullSink(PwGraph.Empty, "GameSink")),
            new RecordingPwLinker(), store, new MockReconciler(), new UiRuleMatcher(), controller);
        await board.InitializeAsync();

        await board.DeleteSinkAsync("GameSink", deleteAffectedPolicy: true);

        Assert.Empty(store.Current.VirtualSinks);
        Assert.Equal(new[] { 9 }, controller.Unloads);                    // tagged twin only
        Assert.Contains(controller.Modules, m => m.ModuleIndex == 7);     // user's sink survives
    }
}
