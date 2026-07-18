using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AutoRoute.App.Views;

namespace AutoRoute.App.Design;

/// <summary>
/// Window-free screenshot capture (run via <c>--screenshot [directory]</c>). Boots Avalonia on the
/// headless platform with real Skia drawing, renders the MainWindow over the fixture board, and
/// writes PNGs of the board — including a simulated drag-over state — for docs and PR review.
/// No visible GUI is shown.
/// </summary>
public static class ScreenshotTool
{
    public static int Run(string[] args)
    {
        var idx = Array.IndexOf(args, "--screenshot");
        var dir = idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith('-')
            ? args[idx + 1]
            : "screenshots";
        Directory.CreateDirectory(dir);

        try
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  headless Avalonia setup: " + ex.Message);
            return 1;
        }

        // Deterministic design graph (not the captured fixture) so shots are reproducible, then
        // exercise the board a little so every card/column state is on screen: keep Spotify → Music
        // as Manual, protect the Desktop sink and the capture device.
        var board = DevComposition.CreateInitializedBoard(DesignGraph.Build());
        var spotify = board.Columns.SelectMany(c => c.Cards)
            .FirstOrDefault(c => c.IsUnsaved && string.Equals(c.Title, "Spotify", StringComparison.OrdinalIgnoreCase));
        if (spotify is not null) board.KeepManual(spotify);
        board.ToggleProtectAsync(90).GetAwaiter().GetResult(); // DesktopSink
        board.ToggleProtectAsync(56).GetAwaiter().GetResult(); // PRO X 2 Mono capture

        var window = new MainWindow { DataContext = board, Width = 1400, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Capture(window, Path.Combine(dir, "board.png"));

        // Scroll to the far right where the routed columns (Game / Music / PRO X 2) live.
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(s => s.Extent.Width > s.Viewport.Width);
        if (scroller is not null)
        {
            scroller.Offset = new Vector(scroller.Extent.Width, 0);
            Dispatcher.UIThread.RunJobs();
            Capture(window, Path.Combine(dir, "board-states.png"));
        }

        // Card-state legend: open the toolbar help flyout and capture it. Taken before the
        // drag-over shot so that state's brush transitions can't linger into this frame.
        var help = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == "HelpButton");
        if (help?.Flyout is not null)
        {
            help.Flyout.ShowAt(help);
            Dispatcher.UIThread.RunJobs();
            Capture(window, Path.Combine(dir, "board-help.png"));
            help.Flyout.Hide();
            Dispatcher.UIThread.RunJobs();
        }

        // Simulated drag-over: light up the drop highlight on the last droppable column
        // (visible at the scrolled-right position).
        var column = window.GetVisualDescendants().OfType<SinkColumnView>()
            .Select(v => v.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("column")))
            .LastOrDefault(b => b is not null && Behaviors.DropTargetBehavior.GetAcceptsDrop(b));
        if (column is not null)
        {
            column.SetValue(Behaviors.DropTargetBehavior.IsDragOverProperty, true);

            // Stage the full mid-drag look: dim the grabbed palette entry and float its ghost
            // over the highlighted drop column.
            var zenItem = window.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("paletteItem") &&
                    (b.DataContext as ViewModels.SourceItemViewModel)?.Title == "Zen");
            zenItem?.Classes.Add("dragging");
            if (zenItem is not null)
                Behaviors.DragGhost.Show(zenItem, "Zen", new Point(1150, 300));

            // Let the ghost's 160ms pop-in animation finish: the headless animation clock follows
            // wall time, but frames only render on forced ticks — so wait, then tick.
            System.Threading.Thread.Sleep(500);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            System.Threading.Thread.Sleep(100);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Capture(window, Path.Combine(dir, "board-dragover.png"));

            Behaviors.DragGhost.Hide();
            zenItem?.Classes.Remove("dragging");
            column.SetValue(Behaviors.DropTargetBehavior.IsDragOverProperty, false);
        }

        window.Close();
        Console.WriteLine("SCREENSHOTS: written to " + Path.GetFullPath(dir));
        return 0;
    }

    private static void Capture(Window window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            Console.WriteLine("  FAIL  no frame captured for " + path);
            return;
        }
        frame.Save(path);
        Console.WriteLine("  WROTE " + path);
    }
}
