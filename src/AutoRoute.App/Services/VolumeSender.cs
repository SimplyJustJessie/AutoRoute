using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.PipeWire;

namespace AutoRoute.App.Services;

/// <summary>
/// Coalesces fader drags: a drag emits dozens of values a second, but only the latest per node
/// matters. One pump at a time sends the newest pending value for each node, pauses briefly, and
/// repeats until nothing is pending — so a drag costs a handful of <c>wpctl</c> runs, not hundreds.
/// </summary>
public sealed class VolumeSender
{
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(40);

    private readonly IVolumeController _controller;
    private readonly object _gate = new();
    private readonly Dictionary<int, double> _pending = new();
    private bool _pumping;

    public VolumeSender(IVolumeController controller) => _controller = controller;

    public void SetVolume(IEnumerable<int> nodeIds, double volume)
    {
        lock (_gate)
        {
            foreach (var id in nodeIds) _pending[id] = volume;
            if (_pumping) return;
            _pumping = true;
        }
        _ = PumpAsync();
    }

    public void SetMute(IEnumerable<int> nodeIds, bool muted)
    {
        foreach (var id in nodeIds)
            _ = _controller.SetMuteAsync(id, muted);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            KeyValuePair<int, double>[] batch;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _pumping = false;
                    return;
                }
                batch = _pending.ToArray();
                _pending.Clear();
            }
            try
            {
                await Task.WhenAll(batch.Select(kv => _controller.SetVolumeAsync(kv.Key, kv.Value)))
                    .ConfigureAwait(false);
            }
            catch (Exception) { /* the controller logs; the next value retries */ }
            await Task.Delay(Pace).ConfigureAwait(false);
        }
    }
}
