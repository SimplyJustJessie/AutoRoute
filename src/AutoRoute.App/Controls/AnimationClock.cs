using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;

namespace AutoRoute.App.Controls;

/// <summary>A control the <see cref="AnimationClock"/> steps. Return false once settled.</summary>
internal interface IClockAnimated
{
    bool OnAnimationFrame(TimeSpan now);
}

/// <summary>
/// One shared clock for the meters' easing, at a fixed modest rate. Asking the window for a
/// callback every display frame (RequestAnimationFrame) ran at 240+ frames a second, for levels
/// that only change 25 times a second, and every frame is a full window render. The clock runs
/// only while something is still moving.
/// </summary>
internal static class AnimationClock
{
    /// <summary>Frames a second: above the meters' 25 readings, so their easing still reads as motion.</summary>
    public const int Rate = 30;

    private static readonly List<IClockAnimated> Subscribers = new();
    private static readonly List<IClockAnimated> Stepping = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;

    /// <summary>Frame interval in seconds, for a control's first step.</summary>
    public static double FrameSeconds => 1.0 / Rate;

    public static void Subscribe(IClockAnimated control)
    {
        if (Subscribers.Contains(control)) return;
        Subscribers.Add(control);
        if (_timer is null)
        {
            // Not the (interval, priority, handler) constructor: that one starts at once.
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / Rate) };
            _timer.Tick += (_, _) => Tick();
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    public static void Unsubscribe(IClockAnimated control)
    {
        Subscribers.Remove(control);
        if (Subscribers.Count == 0) _timer?.Stop();
    }

    private static void Tick()
    {
        var now = Clock.Elapsed;
        Stepping.Clear();
        Stepping.AddRange(Subscribers);
        foreach (var c in Stepping)
            if (!c.OnAnimationFrame(now)) Subscribers.Remove(c);
        Stepping.Clear();
        if (Subscribers.Count == 0) _timer?.Stop();
    }
}
