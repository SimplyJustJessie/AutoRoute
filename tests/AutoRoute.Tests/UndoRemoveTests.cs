using System.Linq;
using System.Threading.Tasks;
using AutoRoute.App.Design;
using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;

namespace AutoRoute.Tests;

/// <summary>The undo toast after removing a route puts back exactly what the removal took.</summary>
public sealed class UndoRemoveTests
{
    private static (BoardViewModel Board, MockRuleStore Store, RecordingPwLinker Linker) Board()
    {
        var store = new MockRuleStore();
        var linker = new RecordingPwLinker();
        var board = new BoardViewModel(new MockPwGraphService(DesignGraph.Build()), linker, store,
            new MockReconciler(), new UiRuleMatcher(), new MockSinkController());
        board.InitializeAsync().GetAwaiter().GetResult();
        return (board, store, linker);
    }

    private static SourceCardViewModel Unsaved(BoardViewModel b) =>
        b.Columns.SelectMany(c => c.Cards).First(c => c.State == CardState.Unsaved);

    [Fact]
    public async Task Undoing_an_unsaved_removal_drops_the_suppression_and_relinks_untagged()
    {
        var (board, store, linker) = Board();
        var card = Unsaved(board);

        await board.RemoveCardAsync(card);
        Assert.True(board.HasToast);
        Assert.Contains(card.Title, board.ToastText);
        Assert.Single(store.Current.Suppressions);
        Assert.NotEmpty(linker.Disconnects);

        await board.UndoCommand.ExecuteAsync(null);
        Assert.False(board.HasToast);
        Assert.Empty(store.Current.Suppressions);
        // Exactly the live links that went away come back, untagged (not as AutoRoute-managed links).
        var relinked = linker.Connects.Where(c => c.RuleId == RecordingPwLinker.Unmanaged).ToList();
        Assert.NotEmpty(relinked);
        Assert.All(relinked, c => Assert.Contains(linker.Disconnects, d => d.OutPortId == c.OutPortId && d.InPortId == c.InPortId));
        Assert.DoesNotContain(linker.Connects, c => c.RuleId != RecordingPwLinker.Unmanaged);
    }

    [Fact]
    public async Task Undoing_a_saved_removal_restores_the_same_rule()
    {
        var (board, store, _) = Board();
        await board.SaveCardAsync(Unsaved(board));
        var rule = Assert.Single(store.Current.Rules);
        var card = board.Columns.SelectMany(c => c.Cards).First(c => c.RuleId == rule.Id || c.State == CardState.Managed);

        await board.RemoveCardAsync(card);
        Assert.Empty(store.Current.Rules);

        await board.UndoCommand.ExecuteAsync(null);
        Assert.Equal(rule.Id, Assert.Single(store.Current.Rules).Id);
    }

    [Fact]
    public async Task Dismissing_the_toast_keeps_the_removal()
    {
        var (board, store, _) = Board();
        await board.RemoveCardAsync(Unsaved(board));
        board.DismissToastCommand.Execute(null);
        Assert.False(board.HasToast);
        await board.UndoCommand.ExecuteAsync(null); // nothing left to undo
        Assert.Single(store.Current.Suppressions);
    }
}
