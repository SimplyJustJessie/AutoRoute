using System;

namespace AutoRoute.PipeWire;

/// <summary>
/// Decides which pw-mon output lines represent activity worth a graph reload. pw-mon prints a
/// record header (<c>added:</c>/<c>changed:</c>/<c>removed:</c>) followed by indented fields,
/// including <c>type: PipeWire:Interface:X</c>. Only Node/Port/Link records can affect routing —
/// churn on Clients, Devices, Metadata, Profiler etc. (which a busy desktop emits constantly)
/// must not cost a pw-dump. Unknown shapes fail OPEN (trigger anyway): a pw-mon format drift can
/// only cost extra reloads, never a missed change. Stateful and intentionally not thread-safe:
/// it is fed sequentially from one stdout pump; <see cref="Reset"/> on respawn.
/// </summary>
public sealed class PwMonLineFilter
{
    private bool _awaitingType;

    /// <summary>Forget any half-seen record (call when a fresh pw-mon process starts).</summary>
    public void Reset() => _awaitingType = false;

    /// <summary>True when this line completes evidence that a routing-relevant record changed.</summary>
    public bool ShouldTrigger(string line)
    {
        if (line.StartsWith("removed:", StringComparison.Ordinal))
        {
            // Removal records don't reliably carry a type line (the object is already gone),
            // and removals are rare enough that filtering them isn't worth a missed change.
            _awaitingType = false;
            return true;
        }

        if (line.StartsWith("added:", StringComparison.Ordinal)
            || line.StartsWith("changed:", StringComparison.Ordinal))
        {
            // The previous record ended without a type line we recognised — fail open on it.
            var previousUnresolved = _awaitingType;
            _awaitingType = true;
            return previousUnresolved;
        }

        if (!_awaitingType) return false;

        var trimmed = line.TrimStart(' ', '\t');
        if (!trimmed.StartsWith("type:", StringComparison.Ordinal)) return false;

        _awaitingType = false;
        return trimmed.Contains("Interface:Node", StringComparison.Ordinal)
            || trimmed.Contains("Interface:Port", StringComparison.Ordinal)
            || trimmed.Contains("Interface:Link", StringComparison.Ordinal);
    }
}
