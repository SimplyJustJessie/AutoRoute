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

        CheckCreateFlyoutRegression(window, board, Check);
        CheckHorizontalContainment(window, Check);

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

    /// <summary>
    /// Long-name containment: the design graph deliberately contains a real-world-length device
    /// name; no palette item and no column card may paint past its panel's right edge (they did —
    /// titles trimmed against a wider constraint and spilled out of the board).
    /// </summary>
    private static void CheckHorizontalContainment(Window window, Action<bool, string> check)
    {
        var offenders = new List<string>();

        void Contained(Visual container, string label, IEnumerable<Visual> items)
        {
            foreach (var item in items)
            {
                var topRight = item.TranslatePoint(new Point(item.Bounds.Width, 0), container);
                if (topRight is { } p && p.X > container.Bounds.Width + 0.5)
                    offenders.Add($"{label} {item.GetType().Name}: right edge {p.X:0.#} > panel width {container.Bounds.Width:0.#}");
            }
        }

        // Check the item/card borders AND every TextBlock: a text can be measured wider than its
        // card (the ScrollViewer.Padding bug) and paint past the panel while the card itself fits.
        foreach (var palette in window.GetVisualDescendants().OfType<PaletteView>())
            Contained(palette, "palette", palette.GetVisualDescendants()
                .Where(v => v is TextBlock || (v is Border b && b.Classes.Contains("paletteItem"))));

        foreach (var column in window.GetVisualDescendants().OfType<SinkColumnView>())
            Contained(column, "column", column.GetVisualDescendants()
                .Where(v => v is TextBlock || (v is Border b && b.Classes.Contains("card"))));

        check(offenders.Count == 0, "long names stay inside their panels (palette + columns)");
        foreach (var o in offenders.Take(4)) Console.WriteLine("        · " + o);
    }

    /// <summary>
    /// Regression for the dead flyout action buttons: Avalonia's Button.OnClick raises Click FIRST
    /// and reads/invokes the bound Command AFTER — a Click handler that synchronously hid the
    /// flyout tore down the popup DataContext and nulled the Command before the invoke. We mimic
    /// that exact ordering: raise Click (runs the code-behind hide, now deferred), then assert the
    /// Command is still alive and execute it, then assert the submit actually took effect.
    /// </summary>
    private static void CheckCreateFlyoutRegression(
        Window window, ViewModels.BoardViewModel board, Action<bool, string> check)
    {
        var newSinkButton = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == "NewSinkButton");
        check(newSinkButton is not null, "'+ New Sink' button rendered");
        if (newSinkButton?.Flyout is not Flyout flyout) return;

        flyout.ShowAt(newSinkButton);
        Dispatcher.UIThread.RunJobs();
        board.NewSinkName = "SmokeSink";
        Dispatcher.UIThread.RunJobs();

        var create = (flyout.Content as Control)?.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content as string == "Create");
        check(create is not null, "Create button present in the open flyout");
        if (create is null) return;

        create.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var command = create.Command; // Button.OnClick reads the Command AFTER the Click event
        check(command is not null && command.CanExecute(null),
            "create Command survives the Click handler (flyout-hide teardown regression)");

        command?.Execute(null);
        Dispatcher.UIThread.RunJobs();
        // SubmitNewSink clears the form only after a successful CreateSinkAsync round-trip.
        check(board.NewSinkName.Length == 0, "flyout Create actually submitted (form reset)");
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly List<string> _sink;
        public RecordingTraceListener(List<string> sink) => _sink = sink;
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) _sink.Add(message!); }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) _sink.Add(message!); }
    }
}
