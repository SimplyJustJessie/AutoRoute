using System;
using System.Collections.Generic;

namespace AutoRoute.PipeWire;

/// <summary>
/// Decides which pw-mon output lines represent activity worth a graph reload. pw-mon prints a
/// record header (<c>added:</c>/<c>changed:</c>/<c>removed:</c>) followed by indented fields,
/// including <c>id: N</c> and <c>type: PipeWire:Interface:X</c>. Only Node/Port/Link records can
/// affect routing — churn on Clients, Devices, Metadata, Profiler etc. (which a busy desktop
/// emits constantly) must not cost a pw-dump.
///
/// Removal records carry no type line (the object is already gone), so the filter remembers the
/// relevance of every added id and looks it up on removal — otherwise transient clients that
/// connect and disconnect several times a second (observed in the wild) would trigger a reload
/// on every disconnect, forever. A removal of an id that was never added fails OPEN, as does any
/// record whose id/type lines never arrive before the next header: format drift can only cost
/// extra reloads, never a missed change. Stateful and intentionally not thread-safe: it is fed
/// sequentially from one stdout pump; <see cref="Reset"/> on respawn.
/// </summary>
public sealed class PwMonLineFilter
{
    /// <summary>Safety valve: a PipeWire graph holds a few hundred objects; growth beyond this means drift.</summary>
    private const int MaxTrackedIds = 65_536;

    private enum Kind { None, Added, Changed, Removed }

    private readonly Dictionary<int, bool> _relevantById = new();
    private Kind _kind = Kind.None;
    private int _id = -1;
    private bool _decided = true;

    /// <summary>Forget any half-seen record and all remembered ids (call when a fresh pw-mon process starts).</summary>
    public void Reset()
    {
        _kind = Kind.None;
        _id = -1;
        _decided = true;
        _relevantById.Clear();
    }

    /// <summary>True when this line completes evidence that a routing-relevant record changed.</summary>
    public bool ShouldTrigger(string line)
    {
        if (line.StartsWith("added:", StringComparison.Ordinal)) return StartRecord(Kind.Added);
        if (line.StartsWith("changed:", StringComparison.Ordinal)) return StartRecord(Kind.Changed);
        if (line.StartsWith("removed:", StringComparison.Ordinal)) return StartRecord(Kind.Removed);

        if (_decided) return false;

        // Changed properties are marked with a leading '*' in pw-mon output.
        var field = line.AsSpan().TrimStart(" \t*");

        if (_id < 0 && field.StartsWith("id: ", StringComparison.Ordinal))
        {
            if (!int.TryParse(field["id: ".Length..], out _id))
            {
                _id = -1; // unparseable id: stay undecided and let the next header fail open
                return false;
            }

            if (_kind == Kind.Removed)
            {
                _decided = true;
                // Forgetting on removal keeps the map bounded to live objects; unknown => fail open.
                if (!_relevantById.Remove(_id, out var relevant))
                    relevant = true;
                return relevant;
            }
            return false;
        }

        if (field.StartsWith("type: ", StringComparison.Ordinal))
        {
            _decided = true;
            var relevant = field.Contains("Interface:Node", StringComparison.Ordinal)
                        || field.Contains("Interface:Port", StringComparison.Ordinal)
                        || field.Contains("Interface:Link", StringComparison.Ordinal);

            if (_id >= 0)
            {
                if (_relevantById.Count >= MaxTrackedIds) _relevantById.Clear();
                _relevantById[_id] = relevant;
            }
            return relevant;
        }

        return false;
    }

    private bool StartRecord(Kind kind)
    {
        // The previous record ended without the lines needed to classify it — fail open on it.
        var previousUnresolved = !_decided;
        _kind = kind;
        _id = -1;
        _decided = false;
        return previousUnresolved;
    }
}
