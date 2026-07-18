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
    public static BoardViewModel CreateInitializedBoard()
    {
        var graph = FixtureLocator.LoadGraphOrDesign();
        return CreateInitializedBoard(graph);
    }

    /// <summary>Build a board over an explicit graph (used by the smoke test) and initialize it.</summary>
    public static BoardViewModel CreateInitializedBoard(PwGraph graph)
    {
        var board = new BoardViewModel(
            new MockPwGraphService(graph),
            new RecordingPwLinker(),
            new MockRuleStore(),
            new MockReconciler(),
            new UiRuleMatcher());

        // Mock services complete synchronously, so this returns with the board already rendered.
        board.InitializeAsync().GetAwaiter().GetResult();
        return board;
    }
}
