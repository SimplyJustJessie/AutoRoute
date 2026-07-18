using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using AutoRoute.App.Hosting;

namespace AutoRoute.Tests;

/// <summary>
/// Verifies the single-instance handshake (ADR-0005) in isolation — the part that can be checked
/// without a display: a second launch's "reveal" message reaches the first instance, the second
/// does not acquire the socket (so it would exit), and a stale socket left by an unclean exit is
/// reclaimed.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    private static string TempSocketPath()
        => Path.Combine("/tmp", "ar-si-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

    [Fact]
    public async Task Second_instance_reveal_reaches_the_primary_and_secondary_does_not_acquire()
    {
        var path = TempSocketPath();
        var revealed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var primary = new SingleInstanceGuard(path);
        var primaryAcquired = await primary.TryAcquireAsync(() => revealed.TrySetResult());
        Assert.True(primaryAcquired, "the first instance should acquire the socket");

        await using var secondary = new SingleInstanceGuard(path);
        var secondaryAcquired = await secondary.TryAcquireAsync(() => { });
        Assert.False(secondaryAcquired, "a second instance must not acquire while the primary is live");

        var delivered = await secondary.SignalRevealAsync();
        Assert.True(delivered, "the reveal message should be delivered to the primary");

        var completed = await Task.WhenAny(revealed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(completed, revealed.Task),
            "the primary should have received the reveal callback");
    }

    [Fact]
    public async Task Stale_socket_from_an_unclean_exit_is_reclaimed()
    {
        var path = TempSocketPath();

        // Simulate a socket file left behind by a crashed primary: bind + listen, then drop the
        // handle without unlinking the filesystem entry.
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(path));
            dead.Listen(1);
        }
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty); // guarantee a leftover exists
        Assert.True(File.Exists(path), "a stale socket file should be present before reclaim");

        await using var guard = new SingleInstanceGuard(path);
        var acquired = await guard.TryAcquireAsync(() => { });
        Assert.True(acquired, "a stale socket should be unlinked and rebound by the new primary");

        // And once reclaimed it behaves as a live primary: a reveal is delivered.
        await using var secondary = new SingleInstanceGuard(path);
        Assert.False(await secondary.TryAcquireAsync(() => { }));
        Assert.True(await secondary.SignalRevealAsync());
    }

    [Fact]
    public async Task Signal_reveal_returns_false_when_no_primary_is_running()
    {
        var path = TempSocketPath();
        await using var guard = new SingleInstanceGuard(path);
        Assert.False(await guard.SignalRevealAsync(), "no primary → nothing to reveal");
    }

    [Fact]
    public async Task Releasing_the_primary_unlinks_the_socket()
    {
        var path = TempSocketPath();
        var guard = new SingleInstanceGuard(path);
        Assert.True(await guard.TryAcquireAsync(() => { }));
        Assert.True(File.Exists(path));

        await guard.DisposeAsync();
        Assert.False(File.Exists(path), "disposing the primary should unlink the socket file");
    }
}
