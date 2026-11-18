using System.Runtime.InteropServices;

namespace AutoRoute.PipeWire.Meters;

/// <summary>
/// The slice of libpulse's asynchronous API the level meter needs. pipewire-pulse serves it, so
/// this talks to PipeWire; libpulse itself ships with every PipeWire desktop (pactl links it).
/// Every call except the threaded-mainloop lifecycle must be made with the mainloop lock held.
/// </summary>
internal static unsafe partial class PulseNative
{
    private const string Lib = "libpulse.so.0";

    public const int SampleFloat32Le = 5;          // PA_SAMPLE_FLOAT32LE
    public const int ContextNoAutospawn = 1;       // PA_CONTEXT_NOAUTOSPAWN

    public const int ContextReady = 4;             // PA_CONTEXT_READY
    public const int ContextFailed = 5;            // PA_CONTEXT_FAILED
    public const int ContextTerminated = 6;        // PA_CONTEXT_TERMINATED

    public const int StreamReady = 2;              // PA_STREAM_READY
    public const int StreamFailed = 3;             // PA_STREAM_FAILED
    public const int StreamTerminated = 4;         // PA_STREAM_TERMINATED

    public const int StreamDontMove = 0x200;                // PA_STREAM_DONT_MOVE
    public const int StreamPeakDetect = 0x800;              // PA_STREAM_PEAK_DETECT
    public const int StreamAdjustLatency = 0x2000;          // PA_STREAM_ADJUST_LATENCY
    public const int StreamDontInhibitAutoSuspend = 0x8000; // PA_STREAM_DONT_INHIBIT_AUTO_SUSPEND

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_new")]
    public static partial IntPtr ThreadedMainloopNew();

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_get_api")]
    public static partial IntPtr ThreadedMainloopGetApi(IntPtr m);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_start")]
    public static partial int ThreadedMainloopStart(IntPtr m);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_stop")]
    public static partial void ThreadedMainloopStop(IntPtr m);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_free")]
    public static partial void ThreadedMainloopFree(IntPtr m);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_lock")]
    public static partial void ThreadedMainloopLock(IntPtr m);

    [LibraryImport(Lib, EntryPoint = "pa_threaded_mainloop_unlock")]
    public static partial void ThreadedMainloopUnlock(IntPtr m);

    [LibraryImport(Lib, EntryPoint = "pa_proplist_new")]
    public static partial IntPtr ProplistNew();

    [LibraryImport(Lib, EntryPoint = "pa_proplist_sets", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ProplistSets(IntPtr p, string key, string value);

    [LibraryImport(Lib, EntryPoint = "pa_proplist_free")]
    public static partial void ProplistFree(IntPtr p);

    [LibraryImport(Lib, EntryPoint = "pa_context_new_with_proplist", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr ContextNewWithProplist(IntPtr api, string name, IntPtr proplist);

    [LibraryImport(Lib, EntryPoint = "pa_context_connect", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ContextConnect(IntPtr c, string? server, int flags, IntPtr spawnApi);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_state")]
    public static partial int ContextGetState(IntPtr c);

    [LibraryImport(Lib, EntryPoint = "pa_context_disconnect")]
    public static partial void ContextDisconnect(IntPtr c);

    [LibraryImport(Lib, EntryPoint = "pa_context_unref")]
    public static partial void ContextUnref(IntPtr c);

    [LibraryImport(Lib, EntryPoint = "pa_stream_new_with_proplist", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StreamNewWithProplist(IntPtr c, string name, SampleSpec* ss, IntPtr channelMap, IntPtr proplist);

    [LibraryImport(Lib, EntryPoint = "pa_stream_set_monitor_stream")]
    public static partial int StreamSetMonitorStream(IntPtr s, uint sinkInputIndex);

    [LibraryImport(Lib, EntryPoint = "pa_stream_set_read_callback")]
    public static partial void StreamSetReadCallback(IntPtr s, delegate* unmanaged[Cdecl]<IntPtr, nuint, IntPtr, void> cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "pa_stream_connect_record", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int StreamConnectRecord(IntPtr s, string? device, BufferAttr* attr, int flags);

    [LibraryImport(Lib, EntryPoint = "pa_stream_get_state")]
    public static partial int StreamGetState(IntPtr s);

    [LibraryImport(Lib, EntryPoint = "pa_stream_peek")]
    public static partial int StreamPeek(IntPtr s, void** data, nuint* nbytes);

    [LibraryImport(Lib, EntryPoint = "pa_stream_drop")]
    public static partial int StreamDrop(IntPtr s);

    [LibraryImport(Lib, EntryPoint = "pa_stream_disconnect")]
    public static partial int StreamDisconnect(IntPtr s);

    [LibraryImport(Lib, EntryPoint = "pa_stream_unref")]
    public static partial void StreamUnref(IntPtr s);
}
