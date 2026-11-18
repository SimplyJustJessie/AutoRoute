using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire.Meters;

/// <summary>What a meter listens to.</summary>
public enum MeterTap
{
    /// <summary>A sink's monitor (<c>&lt;node.name&gt;.monitor</c>). Taken before the sink's own volume.</summary>
    SinkMonitor,

    /// <summary>A capture source by <c>node.name</c>. Taken after the source's volume.</summary>
    Source,

    /// <summary>One app playback stream, by <c>object.serial</c> (pulse's sink-input index). After the stream's volume.</summary>
    PlaybackStream,
}

/// <param name="Tap">Kind of tap.</param>
/// <param name="Name">Sink or source <c>node.name</c>; null for a playback stream.</param>
/// <param name="Serial">The stream's <c>object.serial</c>; 0 otherwise.</param>
public readonly record struct MeterKey(MeterTap Tap, string? Name, uint Serial)
{
    public static MeterKey SinkMonitor(string nodeName) => new(MeterTap.SinkMonitor, nodeName, 0);
    public static MeterKey Source(string nodeName) => new(MeterTap.Source, nodeName, 0);
    public static MeterKey Stream(uint serial) => new(MeterTap.PlaybackStream, null, serial);
}

/// <summary>
/// Live peak levels for a set of nodes. Owned by the UI thread: every member is called from one
/// thread. Costs nothing until <see cref="SetTargets"/> asks for something, and nothing again
/// after <see cref="Stop"/>.
/// </summary>
public interface ILevelMeter : IDisposable
{
    /// <summary>Open a meter for each key and close the ones no longer listed.</summary>
    void SetTargets(IReadOnlyCollection<MeterKey> keys);

    /// <summary>The loudest peak (linear, 0..1+) since the last call, or false if none arrived.</summary>
    bool TryTake(MeterKey key, out float peak);

    /// <summary>Close every meter and the server connection.</summary>
    void Stop();
}

/// <summary>
/// <see cref="ILevelMeter"/> over pipewire-pulse peak-detect record streams, the way pavucontrol
/// meters: one 1-channel float stream per tap at 25 Hz with <c>PEAK_DETECT</c>, which pipewire-pulse
/// turns into a peak resampler, so the server hands over one peak value per 40 ms and nothing else.
/// The streams are passive (they never wake a suspended device) and pinned (<c>DONT_MOVE</c>: when
/// the app stops, the meter dies with it instead of wandering onto the default device). Each is
/// tagged <see cref="MeterProperty"/> so the graph reader can leave it out of the board.
/// <para>
/// Without libpulse (or without a server) every call is a no-op.
/// </para>
/// </summary>
public sealed unsafe class PulseLevelMeter : ILevelMeter
{
    /// <summary>Stream property marking a meter stream; <see cref="PwDumpReader"/> drops nodes carrying it.</summary>
    public const string MeterProperty = "autoroute.meter";

    private const uint Rate = 25; // readings per second, as pavucontrol
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    private readonly ILogger _log;
    private readonly Dictionary<MeterKey, Slot> _slots = new();
    private readonly Dictionary<MeterKey, long> _failedAt = new();

    private IntPtr _mainloop;
    private IntPtr _context;
    private long _contextFailedAt = long.MinValue / 2;
    private bool _unavailable;

    public PulseLevelMeter(ILogger<PulseLevelMeter>? log = null)
    {
        _log = log ?? NullLogger<PulseLevelMeter>.Instance;
    }

    /// <summary>One open record stream. Its peak cell is written by the pulse thread.</summary>
    private sealed class Slot
    {
        private const int Empty = -1; // not a non-negative float's bit pattern

        public IntPtr Stream;
        public GCHandle Handle;
        private int _pending = Empty;

        // Non-negative floats order the same as their int bit patterns, so an int max is a float max.
        public void Offer(float peak)
        {
            var bits = BitConverter.SingleToInt32Bits(Math.Max(0, peak));
            int cur;
            do
            {
                cur = Volatile.Read(ref _pending);
                if (cur != Empty && cur >= bits) return;
            } while (Interlocked.CompareExchange(ref _pending, bits, cur) != cur);
        }

        public bool TryTake(out float peak)
        {
            var bits = Interlocked.Exchange(ref _pending, Empty);
            peak = bits == Empty ? 0 : BitConverter.Int32BitsToSingle(bits);
            return bits != Empty;
        }
    }

    public void SetTargets(IReadOnlyCollection<MeterKey> keys)
    {
        if (_unavailable) return;
        if (keys.Count == 0 && _slots.Count == 0) return;

        try
        {
            if (!EnsureContext()) return;

            PulseNative.ThreadedMainloopLock(_mainloop);
            try
            {
                var state = PulseNative.ContextGetState(_context);
                if (state is PulseNative.ContextFailed or PulseNative.ContextTerminated)
                {
                    _log.LogInformation("Level meter lost the sound server; reconnecting in {Delay}", RetryAfter);
                    _contextFailedAt = Environment.TickCount64;
                    CloseAllLocked();
                    DropContextLocked();
                    return;
                }
                if (state != PulseNative.ContextReady) return; // still connecting; next call applies

                var now = Environment.TickCount64;
                var wanted = keys as IReadOnlySet<MeterKey> ?? keys.ToHashSet();

                // Close what is no longer wanted, and streams that died (app quit, device gone).
                foreach (var (key, slot) in _slots.ToArray())
                {
                    var st = PulseNative.StreamGetState(slot.Stream);
                    var dead = st is PulseNative.StreamFailed or PulseNative.StreamTerminated;
                    if (dead) _failedAt[key] = now;
                    if (dead || !wanted.Contains(key))
                    {
                        CloseLocked(slot);
                        _slots.Remove(key);
                    }
                }

                foreach (var key in _failedAt.Keys.ToArray())
                    if (!wanted.Contains(key)) _failedAt.Remove(key);

                foreach (var key in wanted)
                {
                    if (_slots.ContainsKey(key)) continue;
                    if (_failedAt.TryGetValue(key, out var at) && now - at < RetryAfter.TotalMilliseconds) continue;
                    var slot = OpenLocked(key);
                    if (slot is null) _failedAt[key] = now;
                    else
                    {
                        _failedAt.Remove(key);
                        _slots[key] = slot;
                    }
                }
            }
            finally
            {
                PulseNative.ThreadedMainloopUnlock(_mainloop);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            _log.LogInformation("libpulse not available; level meters are off ({Message})", ex.Message);
        }
    }

    public bool TryTake(MeterKey key, out float peak)
    {
        if (_slots.TryGetValue(key, out var slot)) return slot.TryTake(out peak);
        peak = 0;
        return false;
    }

    /// <summary>Number of open meter streams (for tests and diagnostics).</summary>
    public int OpenCount => _slots.Count;

    public void Stop()
    {
        if (_mainloop == IntPtr.Zero) return;
        PulseNative.ThreadedMainloopLock(_mainloop);
        try
        {
            CloseAllLocked();
            DropContextLocked();
        }
        finally
        {
            PulseNative.ThreadedMainloopUnlock(_mainloop);
        }
        PulseNative.ThreadedMainloopStop(_mainloop);
        PulseNative.ThreadedMainloopFree(_mainloop);
        _mainloop = IntPtr.Zero;
        _failedAt.Clear();
    }

    public void Dispose() => Stop();

    private bool EnsureContext()
    {
        if (_context != IntPtr.Zero) return true;
        if (Environment.TickCount64 - _contextFailedAt < RetryAfter.TotalMilliseconds) return false;

        if (_mainloop == IntPtr.Zero)
        {
            _mainloop = PulseNative.ThreadedMainloopNew();
            if (_mainloop == IntPtr.Zero) return false;
            if (PulseNative.ThreadedMainloopStart(_mainloop) < 0)
            {
                PulseNative.ThreadedMainloopFree(_mainloop);
                _mainloop = IntPtr.Zero;
                return false;
            }
        }

        PulseNative.ThreadedMainloopLock(_mainloop);
        try
        {
            var props = PulseNative.ProplistNew();
            try
            {
                PulseNative.ProplistSets(props, "application.name", "AutoRoute");
                PulseNative.ProplistSets(props, "application.id", "autoroute");
                PulseNative.ProplistSets(props, "application.icon_name", "autoroute");
                _context = PulseNative.ContextNewWithProplist(PulseNative.ThreadedMainloopGetApi(_mainloop), "AutoRoute", props);
            }
            finally
            {
                PulseNative.ProplistFree(props);
            }
            if (_context == IntPtr.Zero) return false;
            if (PulseNative.ContextConnect(_context, null, PulseNative.ContextNoAutospawn, IntPtr.Zero) < 0)
            {
                _contextFailedAt = Environment.TickCount64;
                DropContextLocked();
                return false;
            }
        }
        finally
        {
            PulseNative.ThreadedMainloopUnlock(_mainloop);
        }
        return true;
    }

    private Slot? OpenLocked(MeterKey key)
    {
        var ss = new PulseNative.SampleSpec { Format = PulseNative.SampleFloat32Le, Rate = Rate, Channels = 1 };
        var props = PulseNative.ProplistNew();
        IntPtr stream;
        try
        {
            PulseNative.ProplistSets(props, MeterProperty, "1");
            PulseNative.ProplistSets(props, "media.name", "Level meter");
            PulseNative.ProplistSets(props, "node.passive", "true");
            // Not plain Stream/Input/Audio: pipewire-pulse would list that as a recording, and
            // desktops would show "AutoRoute is using the microphone" whenever the window is open.
            // WirePlumber still links the /Internal variant to its target.
            PulseNative.ProplistSets(props, "media.class", "Stream/Input/Audio/Internal");
            stream = PulseNative.StreamNewWithProplist(_context, "Level meter", &ss, IntPtr.Zero, props);
        }
        finally
        {
            PulseNative.ProplistFree(props);
        }
        if (stream == IntPtr.Zero) return null;

        string? device = key.Tap switch
        {
            MeterTap.SinkMonitor => key.Name + ".monitor",
            MeterTap.Source => key.Name,
            _ => null,
        };
        if (key.Tap == MeterTap.PlaybackStream && PulseNative.StreamSetMonitorStream(stream, key.Serial) < 0)
        {
            PulseNative.StreamUnref(stream);
            return null;
        }

        var slot = new Slot { Stream = stream };
        slot.Handle = GCHandle.Alloc(slot);
        PulseNative.StreamSetReadCallback(stream, &OnRead, GCHandle.ToIntPtr(slot.Handle));

        var attr = new PulseNative.BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = uint.MaxValue,
            PreBuf = uint.MaxValue,
            MinReq = uint.MaxValue,
            FragSize = sizeof(float),
        };
        const int flags = PulseNative.StreamDontMove | PulseNative.StreamPeakDetect
                          | PulseNative.StreamAdjustLatency | PulseNative.StreamDontInhibitAutoSuspend;
        if (PulseNative.StreamConnectRecord(stream, device, &attr, flags) < 0)
        {
            CloseLocked(slot);
            return null;
        }
        return slot;
    }

    private static void CloseLocked(Slot slot)
    {
        if (slot.Stream != IntPtr.Zero)
        {
            // Callbacks only run with the mainloop lock held, which we hold: once the callback is
            // cleared no further read can touch the slot, so the handle can go.
            PulseNative.StreamSetReadCallback(slot.Stream, null, IntPtr.Zero);
            var st = PulseNative.StreamGetState(slot.Stream);
            if (st is PulseNative.StreamReady or 1 /* creating */)
                PulseNative.StreamDisconnect(slot.Stream);
            PulseNative.StreamUnref(slot.Stream);
            slot.Stream = IntPtr.Zero;
        }
        if (slot.Handle.IsAllocated) slot.Handle.Free();
    }

    private void CloseAllLocked()
    {
        foreach (var slot in _slots.Values) CloseLocked(slot);
        _slots.Clear();
    }

    private void DropContextLocked()
    {
        if (_context == IntPtr.Zero) return;
        PulseNative.ContextDisconnect(_context);
        PulseNative.ContextUnref(_context);
        _context = IntPtr.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnRead(IntPtr stream, nuint nbytes, IntPtr userdata)
    {
        try
        {
            void* data;
            nuint len;
            if (PulseNative.StreamPeek(stream, &data, &len) < 0 || len == 0) return;

            var peak = 0f;
            if (data != null)
            {
                var samples = new ReadOnlySpan<float>(data, (int)(len / sizeof(float)));
                foreach (var s in samples)
                {
                    var a = Math.Abs(s);
                    if (a > peak) peak = a;
                }
            }
            PulseNative.StreamDrop(stream);

            if (data != null && GCHandle.FromIntPtr(userdata).Target is Slot slot)
                slot.Offer(peak);
        }
        catch
        {
            // Never let an exception unwind into libpulse.
        }
    }
}
