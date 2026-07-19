using System;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.PipeWire;

/// <summary>
/// The single owner of the live graph snapshot. Wraps a <see cref="PwDumpReader"/> and an
/// <see cref="IGraphMonitor"/>: reloads on every change signal and republishes via
/// <see cref="GraphUpdated"/> — but only when the reload is structurally different from
/// <see cref="Current"/> (<see cref="PwGraph.StructurallyEquals"/>), so param-only churn never
/// fans out into reconcile passes or board rebuilds. Both the UI and the reconciler read
/// <see cref="Current"/> and subscribe to <see cref="GraphUpdated"/> — there is one shared
/// in-memory graph, no IPC.
/// </summary>
public interface IPwGraphService
{
    /// <summary>The most recent snapshot. <see cref="PwGraph.Empty"/> until the first successful load.</summary>
    PwGraph Current { get; }

    /// <summary>Raised (on a background thread) whenever <see cref="Current"/> has been replaced.</summary>
    event EventHandler<PwGraph>? GraphUpdated;

    /// <summary>Do an initial load, publish it, then start the monitor. Idempotent.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop the monitor.</summary>
    Task StopAsync();

    /// <summary>
    /// Force an immediate reload; returns the latest snapshot. Publishes via
    /// <see cref="GraphUpdated"/> only when the reload actually changed the graph.
    /// </summary>
    Task<PwGraph> RefreshAsync(CancellationToken ct = default);
}
