using System;
using System.Collections.Generic;
using System.Linq;
using AutoRoute.App.ViewModels;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Meters;
using AutoRoute.PipeWire.Models;
using Avalonia.Threading;

namespace AutoRoute.App.Services;

/// <summary>
/// Feeds live levels into the board's faders and playing indicators, and only while someone can see
/// them: <see cref="Active"/> is driven by the window being shown on the Audio tab. Inactive, it holds
/// no meter streams and no server connection and runs no timer, so the app in the tray pays nothing.
/// <para>
/// Every node gets its meter up front, idle or not, so sound shows the moment it starts instead of
/// after a graph refresh and a stream setup. That costs nearly nothing: meter streams are passive,
/// so an idle device stays suspended and an unused mic stays closed, and no data flows until the
/// node runs. New nodes are picked up as soon as the graph changes. Levels are
/// normalised to "before this entry's volume" (<see cref="VolumeNodeViewModel.Level"/>): a sink's
/// monitor already is, while app streams and capture sources are measured after their volume, so
/// those are divided back out. Shown levels rise instantly and fall back gently.
/// </para>
/// </summary>
public sealed class LevelMeterFeed : IDisposable
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);
    private const int PlanEveryTicks = 60;   // safety net; graph changes re-plan straight away
    private const int StaleAfterTicks = 6;   // no peak for ~200 ms: the stream paused, read silence
    private const int SoundHoldTicks = 30;   // keep the bars up for 1 s across gaps between words
    private const double Audible = 0.05;     // fader units after volume: about -78 dB
    private const double Release = 0.05;     // fall per frame, in fader units (~0.7 s from full)

    private readonly ILevelMeter _meter;
    private readonly IPwGraphService _graph;
    private readonly BoardViewModel _board;
    private readonly DispatcherTimer _timer;

    private readonly Dictionary<MeterKey, Reading> _readings = new();
    private readonly List<Entry> _plan = new();
    private readonly HashSet<MeterKey> _keys = new();
    private int _tick;
    private bool _active;

    private sealed class Reading
    {
        public double Value;   // cube root of the last peak
        public int LastTick;
    }

    private sealed class Entry(VolumeNodeViewModel vm, (MeterKey Key, double Scale)[] taps)
    {
        public readonly VolumeNodeViewModel Vm = vm;
        public readonly (MeterKey Key, double Scale)[] Taps = taps;
        public double Shown = vm.Level;
        public int LastSoundTick = int.MinValue / 2;
    }

    public LevelMeterFeed(ILevelMeter meter, IPwGraphService graph, BoardViewModel board)
    {
        _meter = meter;
        _graph = graph;
        _board = board;
        _timer = new DispatcherTimer(Frame, DispatcherPriority.Background, (_, _) => Tick());
    }

    /// <summary>Meters run while true (window visible on the Audio tab).</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (value)
            {
                _tick = 0;
                _graph.GraphUpdated += OnGraphChanged;
                _graph.PresentationUpdated += OnGraphChanged;
                RefreshPlan();
                _timer.Start();
            }
            else
            {
                _graph.GraphUpdated -= OnGraphChanged;
                _graph.PresentationUpdated -= OnGraphChanged;
                _timer.Stop();
                _meter.Stop();
                _readings.Clear();
                foreach (var e in _plan) { e.Vm.Level = 0; e.Vm.IsSounding = false; e.Vm.IsMetered = false; }
                _plan.Clear();
            }
        }
    }

    // Graph events arrive on a background thread; the board rebuilds from the same event first
    // (it subscribed earlier), so by the time this runs the view models match the new graph.
    private void OnGraphChanged(object? sender, PwGraph graph) =>
        Dispatcher.UIThread.Post(() => { if (_active) RefreshPlan(); }, DispatcherPriority.Background);

    private void Tick()
    {
        _tick++;
        if (_tick % PlanEveryTicks == 0) RefreshPlan();

        foreach (var (key, reading) in _readings)
        {
            if (_meter.TryTake(key, out var peak))
            {
                reading.Value = Math.Cbrt(peak);
                reading.LastTick = _tick;
            }
            else if (_tick - reading.LastTick > StaleAfterTicks)
            {
                reading.Value = 0;
            }
        }

        foreach (var e in _plan)
        {
            var raw = 0.0;
            foreach (var (key, scale) in e.Taps)
                if (_readings.TryGetValue(key, out var r))
                    raw = Math.Max(raw, r.Value * scale);
            raw = Math.Min(raw, 1);

            if (!e.Vm.IsMuted && raw * e.Vm.Volume > Audible) e.LastSoundTick = _tick;
            var sounding = _tick - e.LastSoundTick <= SoundHoldTicks;
            if (e.Vm.IsSounding != sounding) e.Vm.IsSounding = sounding;

            e.Shown = raw >= e.Shown ? raw : Math.Max(raw, e.Shown - Release);
            if (e.Shown < 0.004) e.Shown = 0;
            // Every frame that differs: the playing bars step their animation on each change.
            if (e.Vm.Level != e.Shown)
                e.Vm.Level = e.Shown;
        }
    }

    /// <summary>Work out which nodes to meter and open/close meters to match.</summary>
    private void RefreshPlan()
    {
        var graph = _graph.Current;
        var old = _plan.ToDictionary(e => e.Vm);
        _plan.Clear();
        _keys.Clear();

        foreach (var vm in Entries())
        {
            var taps = new List<(MeterKey, double)>();
            foreach (var id in vm.NodeIds)
                if (graph.Node(id) is { } node && TapFor(node) is { } tap)
                {
                    taps.Add(tap);
                    _keys.Add(tap.Key);
                }

            vm.IsMetered = taps.Count > 0;
            if (taps.Count == 0)
            {
                if (vm.Level != 0) vm.Level = 0;
                vm.IsSounding = false;
                continue;
            }
            var entry = new Entry(vm, taps.ToArray());
            if (old.TryGetValue(vm, out var prev))
            {
                entry.Shown = prev.Shown;
                entry.LastSoundTick = prev.LastSoundTick;
            }
            _plan.Add(entry);
        }

        _meter.SetTargets(_keys);

        foreach (var key in _readings.Keys.ToArray())
            if (!_keys.Contains(key)) _readings.Remove(key);
        foreach (var key in _keys)
            if (!_readings.ContainsKey(key)) _readings[key] = new Reading { LastTick = _tick };
    }

    private IEnumerable<VolumeNodeViewModel> Entries()
    {
        foreach (var c in _board.Columns) yield return c;
        foreach (var s in _board.Palette.Items) yield return s;
    }

    /// <summary>The meter for a node, and the factor that turns its reading into "before volume".</summary>
    public static (MeterKey Key, double Scale)? TapFor(PwNode node)
    {
        if (node.MediaClass is not { } mc) return null;

        var postVolume = 1 / Math.Max(node.Info.Volume ?? 1, 0.02);
        // A sink's monitor normally carries the signal before the sink's volume; with
        // monitor.channel-volumes (AutoRoute's virtual sinks) it is after it.
        if (mc.StartsWith("Audio/Sink", StringComparison.Ordinal) && node.NodeName is { } sink)
            return (MeterKey.SinkMonitor(sink), node.Info.MonitorHasVolume ? postVolume : 1);

        if (mc.StartsWith("Audio/Source", StringComparison.Ordinal) && node.NodeName is { } source)
            return (MeterKey.Source(source), postVolume);
        if (mc.StartsWith("Stream/Output/Audio", StringComparison.Ordinal) && node.Info.Serial is { } serial)
            return (MeterKey.Stream(serial), postVolume);
        return null; // recording streams and anything else: no meter
    }

    public void Dispose()
    {
        Active = false;
        _timer.Stop();
        _meter.Dispose();
    }
}
