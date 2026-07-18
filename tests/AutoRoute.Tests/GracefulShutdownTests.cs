using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.App.Hosting;

namespace AutoRoute.Tests;

/// <summary>
/// The teardown path shared by tray Quit and the SIGTERM/SIGINT handlers must run exactly once — a
/// second signal, or a signal racing tray Quit, is a harmless no-op. (The signal registration
/// itself lives in App and needs a desktop lifetime; this verifies the reused, idempotent core.)
/// </summary>
public sealed class GracefulShutdownTests
{
    [Fact]
    public void Runs_teardown_exactly_once_across_repeated_requests()
    {
        var count = 0;
        var shutdown = new GracefulShutdown(() => Interlocked.Increment(ref count));

        Assert.False(shutdown.HasShutDown);
        Assert.True(shutdown.RequestOnce());   // first performs the teardown (e.g. tray Quit or SIGTERM)
        Assert.False(shutdown.RequestOnce());  // signal-then-Quit / a second signal → no-op
        Assert.False(shutdown.RequestOnce());

        Assert.True(shutdown.HasShutDown);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Concurrent_requests_run_teardown_once()
    {
        var count = 0;
        var shutdown = new GracefulShutdown(() => Interlocked.Increment(ref count));

        var tasks = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(shutdown.RequestOnce))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(performed => performed)); // exactly one caller did the teardown
        Assert.Equal(1, count);
    }
}
