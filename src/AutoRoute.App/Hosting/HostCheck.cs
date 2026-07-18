using System;
using AutoRoute.App.ViewModels;
using AutoRoute.PipeWire;
using Microsoft.Extensions.DependencyInjection;

namespace AutoRoute.App.Hosting;

/// <summary>
/// Window-free DI-graph check (run via <c>--check-host</c>). Builds the real host, resolves the
/// full service graph — <see cref="BoardViewModel"/> and <see cref="RoutingWorker"/> from the same
/// provider — and disposes, all <b>without</b> starting the host (so no <c>pw-dump</c>/<c>pw-mon</c>
/// process spawns) and without opening a window. Lets QA confirm composition on the real machine
/// without disturbing the desktop.
/// </summary>
public static class HostCheck
{
    public static int Run(AppOptions options)
    {
        var failures = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label);
            if (!ok) failures++;
        }

        Console.WriteLine($"[check-host] poll={options.Poll} background={options.Background}");

        try
        {
            using var host = HostFactory.Build(options);
            var sp = host.Services;

            var board = sp.GetRequiredService<BoardViewModel>();
            var worker = sp.GetRequiredService<RoutingWorker>();

            Check(board is not null, "BoardViewModel resolves from the real service provider");
            Check(worker is not null, "RoutingWorker resolves from the real service provider");
            Check(ReferenceEquals(worker, sp.GetRequiredService<RoutingWorker>()),
                "RoutingWorker is a singleton (same instance the hosted service runs)");
            Check(ReferenceEquals(board, sp.GetRequiredService<BoardViewModel>()),
                "BoardViewModel is a singleton");

            var graph = sp.GetRequiredService<IPwGraphService>();
            var monitor = sp.GetRequiredService<IGraphMonitor>();
            var expectedMonitor = options.Poll ? typeof(PollingGraphMonitor) : typeof(PwMonMonitor);
            Check(monitor.GetType() == expectedMonitor,
                $"IGraphMonitor is {expectedMonitor.Name} for poll={options.Poll}");
            Check(graph is not null, "IPwGraphService (shared graph) resolves");
            Check(worker!.AutomationEnabled, "Automation defaults to enabled");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  host build/resolve threw: " + ex);
            failures++;
        }

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("HOST CHECK: PASS (DI graph resolves headlessly)");
            return 0;
        }
        Console.WriteLine($"HOST CHECK: FAIL ({failures} check(s) failed)");
        return 1;
    }
}
