using System;
using System.Threading;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Time.Testing;

namespace AutoRoute.Tests;

/// <summary>
/// The debouncer's max-wait bound is what keeps a continuous pw-mon flood from starving the
/// reload (triggers faster than the interval used to re-arm the timer forever) while also
/// capping how often the flood can fire the callback. Driven by FakeTimeProvider — no real
/// clocks, no flakiness.
/// </summary>
public sealed class DebouncerTests
{
    [Fact]
    public void Quiet_period_fires_once_per_burst()
    {
        var time = new FakeTimeProvider();
        var fired = 0;
        using var debouncer = new Debouncer(
            TimeSpan.FromMilliseconds(250), () => Interlocked.Increment(ref fired), time: time);

        debouncer.Trigger();
        time.Advance(TimeSpan.FromMilliseconds(100));
        debouncer.Trigger(); // re-arms
        time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Equal(0, fired);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Continuous_triggers_without_max_wait_starve_the_callback()
    {
        // Documents why maxWait exists: a flood faster than the interval postpones forever.
        var time = new FakeTimeProvider();
        var fired = 0;
        using var debouncer = new Debouncer(
            TimeSpan.FromMilliseconds(250), () => Interlocked.Increment(ref fired), time: time);

        for (var i = 0; i < 50; i++)
        {
            debouncer.Trigger();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Max_wait_bounds_a_continuous_flood()
    {
        var time = new FakeTimeProvider();
        var fired = 0;
        using var debouncer = new Debouncer(
            TimeSpan.FromMilliseconds(250), () => Interlocked.Increment(ref fired),
            maxWait: TimeSpan.FromSeconds(1), time: time);

        // Trigger every 100 ms for 1 s: the quiet period never elapses, the bound does.
        for (var i = 0; i < 10; i++)
        {
            debouncer.Trigger();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Equal(1, fired);

        // The flood continues → exactly one more firing per max-wait window.
        for (var i = 0; i < 10; i++)
        {
            debouncer.Trigger();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Max_wait_does_not_shorten_an_isolated_trigger()
    {
        var time = new FakeTimeProvider();
        var fired = 0;
        using var debouncer = new Debouncer(
            TimeSpan.FromMilliseconds(250), () => Interlocked.Increment(ref fired),
            maxWait: TimeSpan.FromSeconds(1), time: time);

        debouncer.Trigger();
        time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Equal(0, fired);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, fired);

        // The window resets after a firing: the next burst gets a fresh quiet period.
        debouncer.Trigger();
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Trigger_after_dispose_is_a_no_op()
    {
        var time = new FakeTimeProvider();
        var fired = 0;
        var debouncer = new Debouncer(
            TimeSpan.FromMilliseconds(250), () => Interlocked.Increment(ref fired), time: time);
        debouncer.Dispose();

        debouncer.Trigger();
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fired);
    }
}
