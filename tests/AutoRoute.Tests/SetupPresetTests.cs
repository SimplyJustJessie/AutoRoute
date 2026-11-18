using System.Linq;
using System.Threading.Tasks;
using AutoRoute.App.Design;
using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Tests;

/// <summary>The first-run "Start with" card: only for a brand-new setup, gone once chosen.</summary>
public sealed class SetupPresetTests
{
    private static BoardViewModel Board(RulesDocument doc, MockRuleStore? store = null, UiSettings? ui = null)
    {
        var board = new BoardViewModel(new MockPwGraphService(PwGraph.Empty), new RecordingPwLinker(),
            store ?? new MockRuleStore(doc), new MockReconciler(), new UiRuleMatcher(), new MockSinkController(), ui: ui);
        board.InitializeAsync().GetAwaiter().GetResult();
        return board;
    }

    [Fact]
    public async Task A_new_setup_is_offered_presets_and_choosing_one_creates_its_outputs()
    {
        var store = new MockRuleStore();
        var ui = new UiSettings();
        var board = Board(RulesDocument.Empty, store, ui);
        Assert.True(board.ShowSetup);

        var streaming = BoardViewModel.SetupPresets.Single(p => p.Key == "streaming");
        await board.ChooseSetupCommand.ExecuteAsync(streaming);

        Assert.False(board.ShowSetup);
        Assert.True(ui.SetupDone);
        Assert.Equal(new[] { "Game", "Music", "Chat" }, store.Current.VirtualSinks.Select(s => s.Name));
    }

    [Fact]
    public void An_existing_setup_never_sees_it()
    {
        var withSink = RulesDocument.Empty with
        {
            VirtualSinks = new[] { new VirtualSinkSpec("x", "Game", "Game", SinkChannels.Stereo) },
        };
        Assert.False(Board(withSink).ShowSetup);
        Assert.False(Board(RulesDocument.Empty, ui: new UiSettings { SetupDone = true }).ShowSetup);
    }
}
