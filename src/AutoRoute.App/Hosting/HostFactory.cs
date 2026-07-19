using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;
using AutoRoute.Engine;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoRoute.App.Hosting;

/// <summary>
/// The single composition root for the always-on host (replaces the <c>DevComposition</c> mock
/// wiring for the real app). Registers the real PipeWire + Engine services as singletons so the
/// <see cref="RoutingWorker"/> and the <see cref="BoardViewModel"/> share one in-memory graph, plus
/// the <see cref="RoutingWorker"/> hosted service. <c>DevComposition</c> stays in place for the
/// window-free <c>--smoke</c>/<c>--smoke-ui</c> checks and design-time rendering.
/// </summary>
public static class HostFactory
{
    public static IHost Build(AppOptions options)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            // journald-friendly: one line per record, no timestamp (journald stamps its own).
            o.SingleLine = true;
            o.TimestampFormat = null;
        });

        var services = builder.Services;

        // Take process-lifetime ownership away from the default ConsoleLifetime: the Avalonia
        // classic-desktop lifetime + the app's PosixSignalRegistration handle SIGTERM/SIGINT, so a
        // competing ConsoleLifetime signal handler must not fight them. (Registered last → wins.)
        services.AddSingleton<IHostLifetime, NoopHostLifetime>();

        services.AddSingleton(options);

        // --- PipeWire layer -------------------------------------------------------------------
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(sp => new PwDumpReader(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetService<ILogger<PwDumpReader>>()));

        // Change monitor: pw-mon by default, polling fallback under --poll.
        if (options.Poll)
        {
            services.AddSingleton<IGraphMonitor>(sp => new PollingGraphMonitor(
                sp.GetRequiredService<PwDumpReader>(),
                interval: null,
                log: sp.GetService<ILogger<PollingGraphMonitor>>()));
        }
        else
        {
            services.AddSingleton<IGraphMonitor>(sp => new PwMonMonitor(
                sp.GetService<ILogger<PwMonMonitor>>()));
        }

        services.AddSingleton<IPwGraphService>(sp => new PwGraphService(
            sp.GetRequiredService<PwDumpReader>(),
            sp.GetRequiredService<IGraphMonitor>(),
            sp.GetService<ILogger<PwGraphService>>()));

        services.AddSingleton<IPwLinker>(sp => new PwLinker(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetService<ILogger<PwLinker>>()));

        services.AddSingleton<IVirtualSinkController>(sp => new PactlSinkController(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetService<ILogger<PactlSinkController>>()));

        // --- Engine layer ---------------------------------------------------------------------
        services.AddSingleton<IRuleStore>(sp => new RuleStore(
            sp.GetService<ILogger<RuleStore>>()));
        services.AddSingleton<IRuleMatcher, RuleMatcher>();
        services.AddSingleton<IReconciler>(sp => new Reconciler(
            sp.GetRequiredService<IPwLinker>(),
            sp.GetRequiredService<IRuleMatcher>(),
            sp.GetService<ILogger<Reconciler>>()));

        // Virtual sinks (ADR-0011): drop-in for boot persistence + pactl for instant effect.
        services.AddSingleton(sp => new SinkDropInWriter(
            SinkDropInWriter.DefaultPath(),
            sp.GetService<ILogger<SinkDropInWriter>>()));
        services.AddSingleton<ISinkReconciler>(sp => new SinkReconciler(
            sp.GetRequiredService<IVirtualSinkController>(),
            sp.GetRequiredService<SinkDropInWriter>(),
            sp.GetService<ILogger<SinkReconciler>>(),
            // Names other conf files still create at boot are excluded from our drop-in so a
            // pipewire-pulse restart can't double-create them while the legacy file exists.
            externalSinkNames: () => sp.GetRequiredService<PulseConfImporter>().ScanExternalSinkNames()));

        services.AddSingleton(sp => new PulseConfImporter(
            PulseConfImporter.DefaultConfDDirectory(),
            SinkDropInWriter.FileName,
            sp.GetService<ILogger<PulseConfImporter>>()));

        // --- App layer ------------------------------------------------------------------------
        services.AddSingleton<AppNotices>();
        services.AddSingleton<BoardViewModel>();
        services.AddSingleton<RoutingWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<RoutingWorker>());

        return builder.Build();
    }
}
