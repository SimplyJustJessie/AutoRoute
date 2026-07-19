using System.Linq;
using System.Threading.Tasks;
using AutoRoute.App.Design;

namespace AutoRoute.Tests;

public class BoardFilterTests
{
    [Fact]
    public async Task Filtering_by_a_sink_name_shows_everything_feeding_that_sink()
    {
        // Design graph: GameSink column fed by a Zen card. Searching for the SINK must not hide
        // the Zen card — "show me this sink and what feeds it" (a matching column previously
        // rendered empty because its cards were filtered on their own text).
        var board = DevComposition.CreateInitializedBoard(DesignGraph.Build());
        await Task.CompletedTask;

        board.Filter.Text = "GameSink";

        var game = board.Columns.Single(c => c.Key == "GameSink");
        Assert.True(game.IsVisible);
        Assert.All(game.Cards, card => Assert.True(card.IsVisible));
        Assert.NotEmpty(game.Cards);

        // Searching for a SOURCE still narrows: only columns fed by it stay visible, showing the
        // matching card.
        board.Filter.Text = "Zen";
        Assert.True(game.IsVisible);
        Assert.Contains(game.Cards, card => card.IsVisible && card.Title.Contains("Zen"));
    }
}
