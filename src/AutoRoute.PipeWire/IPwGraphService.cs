using System;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.PipeWire;

/// <summary>
/// The single owner of the live graph snapshot. Wraps a <see cref="PwDumpReader"/> and an
/// <see cref="IGraphMonitor"/>: reloads on every change signal and republishes via
/// <see cref="GraphUpdated"/>. Both the UI and the reconciler read <see cref="Current"/> and
/// subscribe to <see cref="GraphUpdated"/> — there is one shared in-memory graph, no IPC.
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

    /// <summary>Force an immediate reload; returns and publishes the new snapshot.</summary>
    Task<PwGraph> RefreshAsync(CancellationToken ct = default);
}
