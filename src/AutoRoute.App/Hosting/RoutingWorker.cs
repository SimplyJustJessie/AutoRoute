using System;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoRoute.App.Hosting;

/// <summary>
/// The always-on reconcile loop (PLAN "Process, tray, autostart", step 7). Loads the rule store,
/// starts the shared graph service, and on every <see cref="IPwGraphService.GraphUpdated"/> or
/// <see cref="IRuleStore.Changed"/> trigger runs <see cref="IReconciler.ReconcileAsync"/> — which is
/// idempotent and self-healing, so firing on every trigger is safe. Bursts are coalesced by a short
/// debounce. The loop is gated behind <see cref="AutomationEnabled"/> (default on): when off it keeps
/// watching but skips reconcile, so the graph is never mutated while the user has automation paused.
///
/// <para>Shares the same singleton <see cref="IPwGraphService"/>/<see cref="IRuleStore"/> instances as
/// the <c>BoardViewModel</c>, so UI edits and reconcile see one in-memory graph — no IPC.</para>
/// </summary>
public sealed class RoutingWorker : BackgroundService
{
    private static readonly TimeSpan ReconcileDebounce = TimeSpan.FromMilliseconds(120);

    private readonly IPwGraphService _graph;
    private readonly IRuleStore _store;
    private readonly IReconciler _reconciler;
    private readonly ILogger<RoutingWorker> _log;

    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private Debouncer? _debouncer;
    private CancellationToken _stopping;
    private int _automationEnabled = 1;

    public RoutingWorker(
        IPwGraphService graph,
        IRuleStore store,
        IReconciler reconciler,
        ILogger<RoutingWorker> log)
    {
        _graph = graph;
        _store = store;
        _reconciler = reconciler;
        _log = log;
    }

    /// <summary>
    /// The tray "Automation Enabled" flag. When flipped on, an immediate reconcile is scheduled so
    /// the graph catches up to whatever changed while automation was paused.
    /// </summary>
    public bool AutomationEnabled
    {
        get => Volatile.Read(ref _automationEnabled) == 1;
        set
        {
            var previous = Interlocked.Exchange(ref _automationEnabled, value ? 1 : 0);
            if (previous == (value ? 1 : 0)) return;

            _log.LogInformation("Automation {State}", value ? "enabled" : "disabled");
            if (value) ScheduleReconcile();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _debouncer = new Debouncer(ReconcileDebounce, () => _ = ReconcileNowAsync());

        // Load rules once (also starts the rules.json FileSystemWatcher), then subscribe BEFORE
        // starting the graph so the initial snapshot triggers the first reconcile.
        await _store.LoadAsync(stoppingToken).ConfigureAwait(false);
        _graph.GraphUpdated += OnGraphUpdated;
        _store.Changed += OnRulesChanged;

        _log.LogInformation("RoutingWorker starting graph service (monitor live)");
        await _graph.StartAsync(stoppingToken).ConfigureAwait(false);

        // Guarantee an initial reconcile against the primed snapshot even if the graph service was
        // started elsewhere first (e.g. the BoardViewModel) and its initial GraphUpdated fired before
        // we subscribed. Idempotent, so a redundant pass here is a harmless no-op.
        ScheduleReconcile();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            _graph.GraphUpdated -= OnGraphUpdated;
            _store.Changed -= OnRulesChanged;
            _log.LogInformation("RoutingWorker stopped");
        }
    }

    private void OnGraphUpdated(object? sender, PwGraph graph) => ScheduleReconcile();

    private void OnRulesChanged(object? sender, RulesDocument document) => ScheduleReconcile();

    private void ScheduleReconcile()
    {
        if (!AutomationEnabled) return;
        _debouncer?.Trigger();
    }

    private async Task ReconcileNowAsync()
    {
        if (!AutomationEnabled || _stopping.IsCancellationRequested) return;

        await _reconcileGate.WaitAsync(_stopping).ConfigureAwait(false);
        try
        {
            if (!AutomationEnabled || _stopping.IsCancellationRequested) return;
            await _reconciler.ReconcileAsync(_graph.Current, _store.Current, _stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "reconcile failed; will retry on the next graph/rule change");
        }
        finally
        {
            try { _reconcileGate.Release(); }
            catch (ObjectDisposedException) { /* disposed during shutdown */ }
        }
    }

    public override void Dispose()
    {
        _debouncer?.Dispose();
        _reconcileGate.Dispose();
        base.Dispose();
    }
}
