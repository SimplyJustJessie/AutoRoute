using System;
using System.Collections.Generic;
using System.Linq;
using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Design;

/// <summary>
/// Headless, window-free smoke check (run via <c>--smoke</c>). Builds the top-level
/// <see cref="BoardViewModel"/> over the captured fixture with recording mocks and asserts the
/// board populates and the core commands work — no visible GUI, so it never disturbs the desktop.
/// </summary>
public static class SmokeTest
{
    public static int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string label)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label);
            if (!ok) failures.Add(label);
        }

        var fixture = FixtureLocator.TryFindFixture();
        PwGraph graph;
        if (fixture is not null)
        {
            Console.WriteLine($"[smoke] fixture: {fixture}");
            graph = FixtureLocator.LoadFrom(fixture);
        }
        else
        {
            Console.WriteLine("[smoke] fixture not found — using in-memory DesignGraph");
            graph = DesignGraph.Build();
        }

        var gsvc = new MockPwGraphService(graph);
        var linker = new RecordingPwLinker();
        var store = new MockRuleStore();
        var board = new BoardViewModel(gsvc, linker, store, new MockReconciler(), new UiRuleMatcher());
        board.InitializeAsync().GetAwaiter().GetResult();

        Console.WriteLine($"[smoke] {board.StatusText}");

        // --- board mirrors the live graph ---
        Check(board.Columns.Count > 0, "board has Target Sink columns");
        Check(board.Palette.Items.Count > 0, "palette has Sources");

        var game = board.Columns.FirstOrDefault(c =>
            string.Equals(c.Title, "GameSink", StringComparison.OrdinalIgnoreCase));
        Check(game is not null, "GameSink column exists");

        var zenItem = board.Palette.Items.FirstOrDefault(i =>
            string.Equals(i.Title, "Zen", StringComparison.OrdinalIgnoreCase));
        Check(zenItem is not null, "Zen appears as a Source in the palette");

        var anyUnsaved = board.Columns.SelectMany(c => c.Cards).Any(card => card.State == CardState.Unsaved);
        Check(anyUnsaved, "external Links render as Unsaved on first launch");

        // --- diff-merge is in-place (same column instance survives a rebuild) ---
        var beforeInstance = game;
        var beforeCount = board.Columns.Count;
        board.RebuildFromCurrent();
        Check(board.Columns.Count == beforeCount, "rebuild keeps column count stable");
        Check(ReferenceEquals(beforeInstance, board.Columns.FirstOrDefault(c =>
            string.Equals(c.Title, "GameSink", StringComparison.OrdinalIgnoreCase))),
            "rebuild updates columns in place (no flicker/rebuild)");

        // --- connect (drop): Zen → GameSink → managed card + Rule + pw-link ops ---
        var zenNode = graph.Nodes.FirstOrDefault(n =>
            string.Equals(n.ApplicationName, "Zen", StringComparison.OrdinalIgnoreCase) && n.HasOutputPorts);
        var gameNode = graph.Nodes.FirstOrDefault(n =>
            string.Equals(n.NodeName, "GameSink", StringComparison.OrdinalIgnoreCase));

        if (zenNode is not null && gameNode is not null && game is not null)
        {
            board.ConnectAsync(zenNode.Id, gameNode.Id).GetAwaiter().GetResult();

            Check(store.Current.Rules.Count >= 1, "connect persists a positive Rule");
            Check(linker.Connects.Count >= 1, "connect issues pw-link connect ops");
            var zenCard = game.Cards.FirstOrDefault(c =>
                string.Equals(c.Title, "Zen", StringComparison.OrdinalIgnoreCase));
            Check(zenCard is not null && zenCard.State == CardState.Managed,
                "Zen becomes a Managed card in the GameSink column");

            // --- fan-out: same Source into a second column stays managed in both ---
            var musicNode = graph.Nodes.FirstOrDefault(n =>
                string.Equals(n.NodeName, "MusicSink", StringComparison.OrdinalIgnoreCase));
            if (musicNode is not null)
            {
                board.ConnectAsync(zenNode.Id, musicNode.Id).GetAwaiter().GetResult();
                var stillInGame = game.Cards.Any(c =>
                    string.Equals(c.Title, "Zen", StringComparison.OrdinalIgnoreCase) && c.State == CardState.Managed);
                Check(stillInGame, "fan-out: Zen stays managed in GameSink after routing to MusicSink too");
            }

            // --- protect: Zen node → Protected state, drops refused ---
            board.ToggleProtectAsync(zenNode.Id).GetAwaiter().GetResult();
            Check(store.Current.Protected.Count >= 1, "protect persists a Protected marker");
            var zenProtected = board.Columns.SelectMany(c => c.Cards)
                .Any(c => string.Equals(c.Title, "Zen", StringComparison.OrdinalIgnoreCase)
                          && c.State == CardState.Protected);
            Check(zenProtected, "protected Zen cards render as Protected");
        }
        else
        {
            Check(false, "found Zen source + GameSink target nodes in the graph");
        }

        // --- filter narrows the palette (matches Title/Subtitle/kind) ---
        board.Filter.Text = "zen";
        var zenVisible = board.Palette.Items
            .Any(i => string.Equals(i.Title, "Zen", StringComparison.OrdinalIgnoreCase) && i.IsVisible);
        var spotifyHidden = board.Palette.Items
            .Where(i => string.Equals(i.Title, "Spotify", StringComparison.OrdinalIgnoreCase))
            .All(i => !i.IsVisible);
        Check(zenVisible && spotifyHidden, "text filter narrows the palette (Zen shown, Spotify hidden)");
        board.Filter.Text = string.Empty;
        Check(board.Palette.Items.All(i => i.IsVisible), "clearing the filter restores all Sources");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("SMOKE: PASS (all checks green)");
            return 0;
        }
        Console.WriteLine($"SMOKE: FAIL ({failures.Count} check(s) failed)");
        return 1;
    }
}
