using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Design;

// =====================================================================================
// === Wave 3 replaces this composition root with the Hosting host + real services.  ===
// ===                                                                               ===
// The UI's only requirement is that a BoardViewModel is constructed with these five  ===
// seams and InitializeAsync() is awaited:                                            ===
//                                                                                    ===
//   new BoardViewModel(IPwGraphService, IPwLinker, IRuleStore, IReconciler, IRuleMatcher)
//                                                                                    ===
// Swap the four Mock* types below for PwGraphService / PwLinker / RuleStore /         ===
// Reconciler, and UiRuleMatcher for the Engine's RuleMatcher. Nothing else changes.  ===
// =====================================================================================

/// <summary>
/// The one place that wires mock/in-memory services into a live <see cref="BoardViewModel"/> for
/// standalone visual development. Isolated here so App.axaml.cs stays a one-liner and Wave 3's
/// handoff is a single-file swap.
/// </summary>
public static class DevComposition
{
    /// <summary>Build a board over the captured fixture (or the design graph fallback) and initialize it.</summary>
    public static BoardViewModel CreateInitializedBoard(TabSelectionState? tabs = null)
    {
        var graph = FixtureLocator.LoadGraphOrDesign();
        return CreateInitializedBoard(graph, tabs);
    }

    /// <summary>Build a board over an explicit graph (used by the smoke test) and initialize it.
    /// Pass the same <paramref name="tabs"/> instance as a sibling Video board's for a working
    /// switcher (see <see cref="CreateInitializedVideoBoard"/>); omit it for a standalone preview.</summary>
    public static BoardViewModel CreateInitializedBoard(PwGraph graph, TabSelectionState? tabs = null)
    {
        // GameSink declared as an AutoRoute-managed virtual sink so previews/smoke exercise the
        // VIRTUAL chip + delete affordance (ADR-0011).
        var seed = Engine.Model.RulesDocument.Empty with
        {
            VirtualSinks = new[]
            {
                new Engine.Model.VirtualSinkSpec("design-gamesink", "GameSink", "Game Sink",
                    Engine.Model.SinkChannels.Stereo),
            },
        };

        var board = new BoardViewModel(
            new MockPwGraphService(graph),
            new RecordingPwLinker(),
            new MockRuleStore(seed),
            new MockReconciler(),
            new UiRuleMatcher(),
            new MockSinkController(),
            tabs: tabs);

        // Mock services complete synchronously, so this returns with the board already rendered.
        board.InitializeAsync().GetAwaiter().GetResult();
        return board;
    }

    /// <summary>Build the Video tab's board over the same fixture graph, mock services only (no sink controller).</summary>
    public static VideoBoardViewModel CreateInitializedVideoBoard(PwGraph graph, TabSelectionState? tabs = null)
    {
        var board = new VideoBoardViewModel(
            new MockPwGraphService(graph),
            new RecordingPwLinker(),
            new MockRuleStore(),
            new MockReconciler(),
            new UiRuleMatcher(),
            tabs: tabs);

        board.InitializeAsync().GetAwaiter().GetResult();
        return board;
    }
}
