using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoRoute.PipeWire;

/// <summary>
/// A source of "the graph changed, reload it" signals. Two implementations:
/// <see cref="PwMonMonitor"/> (event-driven, watches <c>pw-mon</c>) and
/// <see cref="PollingGraphMonitor"/> (periodic <c>pw-dump</c> diff, the <c>--poll</c> fallback).
/// The signal is a bare trigger — consumers reload via <c>PwDumpReader</c>; pw-mon output is
/// NEVER parsed for semantics (CONTEXT.md / PLAN.md).
/// </summary>
public interface IGraphMonitor : IAsyncDisposable
{
    /// <summary>Raised (debounced) when something in the graph changed and a reload is due.</summary>
    event EventHandler? Changed;

    /// <summary>Begin watching. Returns once the monitor is armed; watching continues in the background.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop watching and release the underlying process/timer.</summary>
    Task StopAsync();
}
