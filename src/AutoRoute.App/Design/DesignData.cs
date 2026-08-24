using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;

namespace AutoRoute.App.Design;

/// <summary>
/// Design-time DataContext factory for the XAML previewer (<c>d:DataContext</c>). Builds a fully
/// populated board over the in-memory <see cref="DesignGraph"/> so every view previews with data.
/// Never used at runtime.
/// </summary>
public static class DesignData
{
    /// <summary>Shared by <see cref="Board"/> and <see cref="VideoBoard"/> so the previewed tab pills actually switch.</summary>
    private static readonly TabSelectionState Tabs = new();

    /// <summary>A fully wired board over the deterministic design graph.</summary>
    public static BoardViewModel Board { get; } =
        DevComposition.CreateInitializedBoard(DesignGraph.Build(), Tabs);

    /// <summary>The Video tab's board over the same design graph, for its own previews.</summary>
    public static VideoBoardViewModel VideoBoard { get; } =
        DevComposition.CreateInitializedVideoBoard(DesignGraph.Build(), Tabs);

    /// <summary>The window root VM (Audio + Video) for MainWindow.axaml's design-time preview.</summary>
    public static MainWindowViewModel MainWindow { get; } = new(Board, VideoBoard, Tabs);

    /// <summary>First column (a Target Sink with cards) for SinkColumnView previews.</summary>
    public static SinkColumnViewModel Column => FirstColumn();

    /// <summary>First card for SourceCardView previews.</summary>
    public static SourceCardViewModel Card => FirstCard();

    /// <summary>The sources palette for PaletteView previews.</summary>
    public static SourcesPaletteViewModel Palette => Board.Palette;

    private static SinkColumnViewModel FirstColumn()
    {
        foreach (var c in Board.Columns) return c;
        // Fallback empty column so the previewer never crashes.
        return new SinkColumnViewModel(Board, new ColumnModel(0, "none", "Target", "", false,
            null, false, System.Array.Empty<CardModel>()));
    }

    private static SourceCardViewModel FirstCard()
    {
        foreach (var c in Board.Columns)
            foreach (var card in c.Cards)
                return card;
        return new SourceCardViewModel(Board, new CardModel(
            "none", 0, "none", System.Array.Empty<int>(), "Source", "", CardState.Unsaved,
            null, null, null, "Unsaved external Link"));
    }
}
