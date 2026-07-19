using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.Engine;

/// <inheritdoc cref="ISinkReconciler"/>
public sealed class SinkReconciler : ISinkReconciler
{
    /// <summary>Consecutive-failure delays per sink name — prevents fighting a broken setup.</summary>
    private static readonly TimeSpan[] BackoffDelays =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
    };

    private readonly IVirtualSinkController _sinks;
    private readonly SinkDropInWriter _dropIn;
    private readonly Func<IReadOnlySet<string>>? _externalSinkNames;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private readonly Dictionary<string, (int Failures, DateTimeOffset NextAttempt)> _backoff = new(StringComparer.Ordinal);

    /// <summary>The last policy processed — stale-module cleanup only needs to run when it changes.</summary>
    private RulesDocument? _lastRules;

    /// <summary>Declared names some OTHER conf file also creates at boot (refreshed on stale passes).</summary>
    private IReadOnlySet<string> _shadowed = new HashSet<string>(StringComparer.Ordinal);

    public SinkReconciler(
        IVirtualSinkController sinks,
        SinkDropInWriter dropIn,
        ILogger<SinkReconciler>? log = null,
        TimeProvider? time = null,
        Func<IReadOnlySet<string>>? externalSinkNames = null)
    {
        _sinks = sinks;
        _dropIn = dropIn;
        _externalSinkNames = externalSinkNames;
        _log = log ?? NullLogger<SinkReconciler>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task EnsureAsync(PwGraph graph, RulesDocument rules, CancellationToken ct = default)
    {
        var declared = rules.VirtualSinks;

        // Stale tagged modules can only appear when the declared set changes (or on the first pass
        // after startup, covering leftovers from a previous run) — steady-state passes with nothing
        // missing then cost zero process spawns.
        var checkStale = !ReferenceEquals(rules, _lastRules);
        _lastRules = rules;

        if (checkStale && _externalSinkNames is not null)
        {
            try { _shadowed = _externalSinkNames(); }
            catch (Exception ex) { _log.LogWarning(ex, "external sink-name scan failed; keeping previous set"); }
        }

        try
        {
            // Legacy-shadowed names are EXCLUDED from our drop-in: while another conf file (the
            // user's not-yet-retired static declaration) creates sink X at boot, our drop-in
            // declaring X too would double-create it on every pipewire-pulse start. The sink stays
            // declared/managed; boot-creation just remains the legacy file's job until it's gone.
            var bootOwned = declared.Where(s => !_shadowed.Contains(s.Name)).ToList();
            await _dropIn.SyncAsync(bootOwned, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "drop-in sync failed; will retry next pass");
        }

        var now = _time.GetUtcNow();
        var missing = declared
            .Where(s => !GraphHasSink(graph, s.Name) && !InBackoff(s.Name, now))
            .ToList();

        // Duplicates are born on pipewire-pulse restarts — which change the graph, not the rules —
        // so the snapshot itself must be able to trigger a cleanup pass, not just a policy change.
        var duplicateInGraph = declared.Any(s => CountSinks(graph, s.Name) > 1);
        var runCleanup = checkStale || duplicateInGraph;

        if (missing.Count == 0 && !runCleanup) return;

        IReadOnlyList<NullSinkModule> modules;
        try
        {
            modules = await _sinks.ListNullSinkModulesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "listing null-sink modules failed; will retry next pass");
            return;
        }

        var loadedNames = modules.Select(m => m.SinkName).ToHashSet(StringComparer.Ordinal);
        foreach (var spec in missing)
        {
            ct.ThrowIfCancellationRequested();

            // Module already loaded but node not yet in this snapshot (boot race with the drop-in,
            // or a load from the previous pass) — the node will appear; loading again would
            // duplicate the sink.
            if (loadedNames.Contains(spec.Name)) continue;

            var request = new NullSinkRequest(spec.Name, spec.Description, spec.Channels == SinkChannels.Mono);
            var result = await _sinks.LoadAsync(request, ct).ConfigureAwait(false);
            if (result.Success)
            {
                _backoff.Remove(spec.Name);
            }
            else
            {
                RecordFailure(spec.Name, now);
            }
        }

        if (runCleanup)
        {
            var declaredNames = declared.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            // Only OUR modules (tagged) are ever auto-unloaded; the user's untagged legacy/manual
            // modules are never touched without an explicit UI action.
            foreach (var stale in modules.Where(m => m.IsAutoRouteTagged && !declaredNames.Contains(m.SinkName)))
            {
                ct.ThrowIfCancellationRequested();
                _log.LogInformation("unloading stale managed sink {Name} (module {Index})",
                    stale.SinkName, stale.ModuleIndex);
                await _sinks.UnloadAsync(stale.ModuleIndex, ct).ConfigureAwait(false);
            }

            // Duplicate convergence: a name existing as MULTIPLE modules is broken state (seen
            // live when the legacy conf and an earlier drop-in both booted the same sinks). Keep
            // exactly one — prefer the user's untagged copy (it wins while their file exists),
            // else the lowest-index tagged one — and unload only tagged extras. Links through an
            // unloaded node self-heal via the next link-reconcile pass.
            foreach (var group in modules.GroupBy(m => m.SinkName).Where(g => g.Count() > 1))
            {
                if (!declaredNames.Contains(group.Key)) continue; // undeclared handled above (tagged) or not ours
                var keep = group.FirstOrDefault(m => !m.IsAutoRouteTagged)
                           ?? group.OrderBy(m => m.ModuleIndex).First();
                foreach (var extra in group.Where(m => m.IsAutoRouteTagged && m.ModuleIndex != keep.ModuleIndex))
                {
                    ct.ThrowIfCancellationRequested();
                    _log.LogWarning("sink {Name} exists {Count}x; unloading duplicate managed module {Index}",
                        group.Key, group.Count(), extra.ModuleIndex);
                    await _sinks.UnloadAsync(extra.ModuleIndex, ct).ConfigureAwait(false);
                }
            }

            // Names no longer declared need no backoff bookkeeping.
            foreach (var name in _backoff.Keys.Where(n => !declaredNames.Contains(n)).ToList())
                _backoff.Remove(name);
        }
    }

    private static bool GraphHasSink(PwGraph graph, string name) => CountSinks(graph, name) > 0;

    private static int CountSinks(PwGraph graph, string name) =>
        graph.Nodes.Count(n =>
            string.Equals(n.NodeName, name, StringComparison.Ordinal) &&
            n.MediaClass?.Contains("Audio/Sink", StringComparison.Ordinal) == true);

    private bool InBackoff(string name, DateTimeOffset now) =>
        _backoff.TryGetValue(name, out var state) && now < state.NextAttempt;

    private void RecordFailure(string name, DateTimeOffset now)
    {
        var failures = _backoff.TryGetValue(name, out var state) ? state.Failures + 1 : 1;
        var delay = BackoffDelays[Math.Min(failures, BackoffDelays.Length) - 1];
        _backoff[name] = (failures, now + delay);
        _log.LogWarning("sink {Name} failed to load ({Failures}x); next attempt in {Delay}",
            name, failures, delay);
    }
}
