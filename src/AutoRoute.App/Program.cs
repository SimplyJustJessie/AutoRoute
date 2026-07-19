using Avalonia;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using AutoRoute.App.Hosting;
using Microsoft.Extensions.Hosting;

namespace AutoRoute.App;

sealed class Program
{
    // Once a signal is intercepted (context.Cancel = true) WE own termination. During a full PC
    // restart the session tears down around us — compositor, DBus session bus (the tray needs it),
    // and PipeWire all go away — so the graceful path (Avalonia dispatcher, DBus tray dispose, host
    // StopAsync) can stall. If it does, an intercepted-but-not-exited process blocks the reboot until
    // systemd/logind escalates to SIGKILL (~90s), which the desktop reports as the app preventing
    // shutdown. This watchdog force-exits after a short grace period so a reboot is never held up. A
    // healthy teardown finishes in well under a second, long before the watchdog fires.
    private static readonly TimeSpan ShutdownWatchdogTimeout = TimeSpan.FromSeconds(5);
    private static int _signalCount;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Window-free smoke checks (never pop a GUI onto the user's desktop).
        if (Array.IndexOf(args, "--smoke") >= 0)
            return Design.SmokeTest.Run();
        if (Array.IndexOf(args, "--smoke-ui") >= 0)
            return Design.HeadlessSmoke.Run();
        if (Array.IndexOf(args, "--screenshot") >= 0)
            return Design.ScreenshotTool.Run(args);
        if (Array.IndexOf(args, "--stress") >= 0)
            return Design.StressTest.Run();

        var options = AppOptions.Parse(args);

        // Window-free DI-graph check: build + resolve + dispose the real host, no window, no host
        // Start (so no pw-dump/pw-mon spawn). Diagnostic only — runs before the single-instance guard.
        if (Array.IndexOf(args, "--check-host") >= 0)
            return HostCheck.Run(options);

        // === Single instance (ADR-0005) ===============================================
        // The first process binds the unix socket and owns the sole host/worker/tray. A later
        // launch reaches the primary, asks it to reveal its window, and exits without a host.
        var guard = new SingleInstanceGuard();
        var acquired = guard.TryAcquireAsync(App.RequestReveal).GetAwaiter().GetResult();
        if (!acquired)
        {
            var delivered = guard.SignalRevealAsync().GetAwaiter().GetResult();
            guard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.Error.WriteLine(delivered
                ? "AutoRoute is already running — revealed the existing window."
                : "AutoRoute is already running.");
            return 0;
        }

        // === Clean shutdown on SIGTERM/SIGINT =========================================
        // Only registered here in the real host path (smoke/check-host/secondary all returned
        // above). SIGTERM (systemctl --user stop) and SIGINT (Ctrl-C) funnel into the same graceful
        // teardown as tray Quit; without this the classic-desktop lifetime blocks the main thread and
        // the default disposition would either hang the stop to its timeout or kill us and leave a
        // stale socket. Registered before Avalonia starts so a stop during startup is honoured too.
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal);

        // === Always-on host ============================================================
        // Build + start the host (RoutingWorker loads rules, starts the graph monitor) on the main
        // thread, hand its services to the Avalonia app, then run the classic desktop lifetime. The
        // lifetime blocks until tray Quit or a signal calls Shutdown().
        var host = HostFactory.Build(options);
        try
        {
            host.Start();
            App.Configure(host.Services, options);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Tray Quit / signal path: stop the worker, dispose the host (which disposes the
            // RuleStore's watcher and the graph service's monitor), and unlink the socket.
            host.StopAsync().GetAwaiter().GetResult();
            host.Dispose();
            guard.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return 0;
    }

    private static void OnPosixSignal(PosixSignalContext context)
    {
        // Take over from the default disposition (which would kill us and leave a stale socket) and
        // run the same graceful teardown as tray Quit, marshalled to the UI thread by App.
        context.Cancel = true;

        // A second signal — the user/systemd insisting, or a stop timeout escalating — means "go
        // now": stop intercepting and exit immediately rather than keep the process alive.
        if (Interlocked.Increment(ref _signalCount) > 1)
        {
            Environment.Exit(0);
            return;
        }

        App.RequestShutdown();

        // Cancelling the default disposition made us responsible for exiting; bound that with a
        // watchdog so a stalled teardown during a PC restart can never block the reboot.
        ArmShutdownWatchdog();
    }

    // A background thread that force-exits if the graceful teardown hasn't finished in time. Being a
    // background thread, it can't keep the process alive: a clean exit (Main returns) tears it down
    // with the process, so it only ever fires when the normal path is wedged.
    private static void ArmShutdownWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(ShutdownWatchdogTimeout);
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "shutdown-watchdog",
        };
        watchdog.Start();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
