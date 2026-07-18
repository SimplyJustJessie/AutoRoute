using System;
using System.Threading;

namespace AutoRoute.App.Hosting;

/// <summary>
/// A one-shot guard around the app's teardown so every stop path — tray <b>Quit</b>, a SIGTERM from
/// <c>systemctl --user stop</c>, or a SIGINT from Ctrl-C — runs the <em>same</em> teardown exactly
/// once. A second signal, or a signal racing tray Quit, is a no-op and never double-shuts-down or
/// throws.
/// </summary>
public sealed class GracefulShutdown
{
    private readonly Action _teardown;
    private int _done;

    public GracefulShutdown(Action teardown)
    {
        ArgumentNullException.ThrowIfNull(teardown);
        _teardown = teardown;
    }

    /// <summary>True once the teardown has been performed.</summary>
    public bool HasShutDown => Volatile.Read(ref _done) == 1;

    /// <summary>
    /// Run the teardown if it has not run yet. Returns <c>true</c> when this call performed it,
    /// <c>false</c> when a prior call already did (idempotent, thread-safe).
    /// </summary>
    public bool RequestOnce()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return false;
        _teardown();
        return true;
    }
}
