using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AutoRoute.App.Views;

namespace AutoRoute.App.Design;

/// <summary>
/// Window-free headless render check (run via <c>--smoke-ui</c>). Boots Avalonia on the headless
/// platform, builds the real MainWindow over the fixture board, forces a layout pass, and asserts
/// the visual tree actually materialized columns + palette + cards — while capturing Avalonia's
/// binding/resource warnings so a runtime binding error fails the check. No visible GUI is shown.
/// </summary>
public static class HeadlessSmoke
{
    public static int Run()
    {
        var warnings = new List<string>();
        var listener = new RecordingTraceListener(warnings);
        Trace.Listeners.Add(listener);

        try
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                .LogToTrace(LogEventLevel.Warning)
                .SetupWithoutStarting();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  headless Avalonia setup: " + ex.Message);
            return 1;
        }

        var failures = new List<string>();
        void Check(bool ok, string label)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label);
            if (!ok) failures.Add(label);
        }

        var board = DevComposition.CreateInitializedBoard();
        var window = new MainWindow { DataContext = board };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(1100, 640));
        window.Arrange(new Rect(0, 0, 1100, 640));
        Dispatcher.UIThread.RunJobs();

        var columns = window.GetVisualDescendants().OfType<SinkColumnView>().ToList();
        var palettes = window.GetVisualDescendants().OfType<PaletteView>().ToList();
        var cards = window.GetVisualDescendants().OfType<SourceCardView>().ToList();

        Check(window.GetVisualDescendants().OfType<BoardView>().Any(), "BoardView rendered");
        Check(palettes.Count == 1, "PaletteView rendered");
        Check(columns.Count > 0, $"Target Sink columns rendered ({columns.Count})");
        Check(cards.Count > 0, $"Source cards rendered ({cards.Count})");

        // A GameSink column must be present in the rendered tree.
        var gameRendered = columns.Any(c =>
            c.DataContext is ViewModels.SinkColumnViewModel col && SmokeTest.IsGameSink(col));
        Check(gameRendered, "GameSink column present in the visual tree");

        Trace.Listeners.Remove(listener);

        var bindingErrors = warnings
            .Where(w => w.Contains("Binding", StringComparison.OrdinalIgnoreCase) ||
                        w.Contains("Unable to", StringComparison.OrdinalIgnoreCase) ||
                        w.Contains("not found", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        Check(bindingErrors.Count == 0, "no binding/resource errors at startup");
        foreach (var e in bindingErrors) Console.WriteLine("        · " + e);

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("UI SMOKE: PASS (headless render clean)");
            return 0;
        }
        Console.WriteLine($"UI SMOKE: FAIL ({failures.Count} check(s) failed)");
        return 1;
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly List<string> _sink;
        public RecordingTraceListener(List<string> sink) => _sink = sink;
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) _sink.Add(message!); }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) _sink.Add(message!); }
    }
}
