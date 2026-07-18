using System.Linq;
using System.Threading.Tasks;
using AutoRoute.App.Design;
using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

/// <summary>
/// Regression for the live A4 crash: duplicate same-named sink nodes (legacy conf + drop-in both
/// booting a sink) made repeated board rebuilds throw on a duplicate diff-merge key inside a
/// dispatcher callback and abort the whole app.
/// </summary>
public class BoardViewModelResilienceTests
{
    [Fact]
    public async Task Rebuilds_survive_duplicate_named_sinks()
    {
        var graph = GraphMutations.WithNullSink(
            GraphMutations.WithNullSink(PwGraph.Empty, "DiscordSink", "Discord Sink"),
            "DiscordSink", "Discord Sink");

        var board = new BoardViewModel(
            new MockPwGraphService(graph),
            new RecordingPwLinker(),
            new MockRuleStore(),
            new MockReconciler(),
            new UiRuleMatcher(),
            new MockSinkController());

        await board.InitializeAsync();   // first rebuild inserts both columns
        board.RebuildFromCurrent();      // second rebuild re-merges — this one crashed before

        Assert.Equal(2, board.Columns.Count);
        Assert.Equal(2, board.Columns.Select(c => c.Key).Distinct().Count());
        Assert.DoesNotContain("failed", board.StatusText); // merged cleanly, not via the catch-all
    }
}
