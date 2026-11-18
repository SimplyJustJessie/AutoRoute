using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace AutoRoute.App.Hosting;

/// <summary>
/// Keeps one known-harmless teardown race from aborting the process on exit.
/// <para>
/// Avalonia's FreeDesktop integration (tray StatusNotifierItem, portal settings, …) keeps Tmds.DBus
/// connections whose observers captured the UI <c>SynchronizationContext</c>. When such a
/// connection notices its disconnect <em>after</em> the classic-desktop lifetime has shut the
/// dispatcher down, <c>Observer.Emit</c> calls <c>Dispatcher.Send</c> from a thread-pool thread, the
/// dead dispatcher cancels the operation, and the resulting <see cref="OperationCanceledException"/>
/// escapes Tmds.DBus unhandled. That turned roughly one SIGTERM in six into a SIGABRT core dump, and
/// the abort skipped host disposal, orphaning the <c>pw-mon</c> child.
/// </para>
/// <para>
/// We don't own those connections, so the teardown can't be reordered from here. Instead, once a
/// shutdown has begun, a cancellation thrown out of Avalonia's dispatcher is treated as handled: the
/// UI it was trying to reach is already gone, so there's nothing left to deliver it to. Anything
/// else, and anything before shutdown, stays fatal exactly as before.
/// </para>
/// </summary>
public static class ShutdownExceptionFilter
{
    private static int _shuttingDown;

    /// <summary>True once any stop path (signal, tray Quit, session logout) has started teardown.</summary>
    public static bool IsShuttingDown => Volatile.Read(ref _shuttingDown) == 1;

    /// <summary>Record that teardown has begun. Idempotent and safe from any thread.</summary>
    public static void MarkShuttingDown() => Volatile.Write(ref _shuttingDown, 1);

    /// <summary>
    /// Install the process-wide handler (.NET 10 <see cref="ExceptionHandling"/>). Call once, from
    /// the real host path only; it applies to unhandled exceptions on non-main threads.
    /// </summary>
    public static void Install() => ExceptionHandling.SetUnhandledExceptionHandler(Handle);

    private static bool Handle(Exception ex)
    {
        if (!IsBenignTeardownException(ex, IsShuttingDown)) return false;
        Console.Error.WriteLine(
            "shutdown: ignored a late UI-dispatcher callback after teardown (" + ex.GetType().Name + ")");
        return true;
    }

    /// <summary>
    /// The filter itself, kept pure for tests: a cancellation raised from Avalonia's dispatcher while
    /// shutting down. Everything else returns false and keeps the default crash behaviour.
    /// </summary>
    public static bool IsBenignTeardownException(Exception ex, bool shuttingDown)
    {
        if (!shuttingDown) return false;
        if (ex is not OperationCanceledException) return false;
        var trace = ex.StackTrace;
        return trace is not null && trace.Contains("Avalonia.Threading.Dispatcher", StringComparison.Ordinal);
    }
}
