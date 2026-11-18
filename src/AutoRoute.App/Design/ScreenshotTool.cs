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
        // --live: one read-only pw-dump of this machine's graph instead of the design graph (mock
        // linker + rule store, so nothing is written to PipeWire) — for eyeballing real devices.
        var live = Array.IndexOf(args, "--live") >= 0;
        var graph = live
            ? new AutoRoute.PipeWire.PwDumpReader(new AutoRoute.PipeWire.Process.ProcessRunner()).LoadAsync().GetAwaiter().GetResult()
            : DesignGraph.Build();

        var tabs = new ViewModels.TabSelectionState();
        var board = DevComposition.CreateInitializedBoard(graph, tabs);
        if (!live)
        {
            var spotify = board.Columns.SelectMany(c => c.Cards)
                .FirstOrDefault(c => c.IsUnsaved && string.Equals(c.Title, "Spotify", StringComparison.OrdinalIgnoreCase));
            if (spotify is not null) board.KeepManual(spotify);
            board.ToggleProtectAsync(90).GetAwaiter().GetResult(); // DesktopSink
            board.ToggleProtectAsync(56).GetAwaiter().GetResult(); // PRO X 2 Mono capture
        }

        var videoBoard = DevComposition.CreateInitializedVideoBoard(graph, tabs);
        var mainVm = new ViewModels.MainWindowViewModel(board, videoBoard, tabs);

        // --size WxH: render at a specific window size (default 1400x800).
        double width = 1400, height = 800;
        var sizeIdx = Array.IndexOf(args, "--size");
        if (sizeIdx >= 0 && sizeIdx + 1 < args.Length && args[sizeIdx + 1].Split('x') is [var w, var h]
            && double.TryParse(w, out var pw) && double.TryParse(h, out var ph))
            (width, height) = (pw, ph);

        var window = new MainWindow { DataContext = mainVm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Capture(window, Path.Combine(dir, "board.png"));

        board.Ui.IsCompact = true;
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(dir, "board-compact.png"));
        board.Ui.IsCompact = false;
        Dispatcher.UIThread.RunJobs();

        // Live meters, staged: a few frames of made-up levels into every running entry, so the
        // fader fills and the equaliser bars show their live shape.
        var rng = new Random(7);
        var metered = board.Columns.Cast<ViewModels.VolumeNodeViewModel>()
            .Concat(board.Palette.Items).Where(v => v.IsRunning).ToList();
        foreach (var v in metered) v.IsMetered = true;
        for (var f = 0; f < 12; f++)
            foreach (var v in metered) v.Level = 0.15 + rng.NextDouble() * 0.8;
        Dispatcher.UIThread.RunJobs();
        if (metered.Count > 0) // the design graph has nothing running; --live usually does
            Capture(window, Path.Combine(dir, "board-meters.png"));
        foreach (var v in metered) { v.Level = 0; v.IsMetered = false; }
        Dispatcher.UIThread.RunJobs();

        // The Video tab: switch to it and capture the VTube Studio → OBS routing on its own board.
        var videoTab = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content as string == "Video");
        if (videoTab is not null)
        {
            videoTab.Command?.Execute(null); // OnClick invokes the Command; raising Click alone would not
            Dispatcher.UIThread.RunJobs();
            Capture(window, Path.Combine(dir, "board-video.png"));

            var audioTab = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Content as string == "Audio");
            audioTab?.Command?.Execute(null); // back to Audio for the rest of the shots below
            Dispatcher.UIThread.RunJobs();
        }

        // Simulated drag-over: light up the drop highlight on the last droppable row.
        var column = window.GetVisualDescendants().OfType<SinkColumnView>()
            .Select(v => v.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("rowHost")))
            .LastOrDefault(b => b is not null && Behaviors.DropTargetBehavior.GetAcceptsDrop(b));
        if (column is not null)
        {
            column.SetValue(Behaviors.DropTargetBehavior.IsDragOverProperty, true);

            // Stage the full mid-drag look: dim the grabbed palette entry and float its ghost
            // over the highlighted drop row.
            var zenItem = window.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("srcEntry") &&
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
