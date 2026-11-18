using System;
using System.Runtime.CompilerServices;
using AutoRoute.App.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AutoRoute.App.Behaviors;

/// <summary>
/// Eased mouse-wheel scrolling for a <see cref="ScrollViewer"/>, with scroll latching.
/// <para>
/// Wheel notches move a target offset and the viewer glides towards it each frame instead of
/// jumping a full step. Touchpads (fractional, high-rate deltas) scroll 1:1 without easing.
/// </para>
/// <para>
/// Latching (what browsers do): once a wheel gesture is scrolling this list, later wheel events in
/// that gesture keep scrolling it even when the pointer passes over a <see cref="Fader"/>, so a
/// fast scroll no longer changes some volume on the way past. A fader only gets the wheel when the
/// gesture <em>starts</em> on it after the pointer has rested there (<see cref="Fader.WantsWheel"/>).
/// </para>
/// </summary>
public static class SmoothScroll
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(SmoothScroll));

    public static void SetIsEnabled(ScrollViewer s, bool v) => s.SetValue(IsEnabledProperty, v);
    public static bool GetIsEnabled(ScrollViewer s) => s.GetValue(IsEnabledProperty);

    private const double NotchPixels = 90;          // one wheel notch
    private const double Ease = 0.22;               // fraction of the remaining distance per frame
    private static readonly TimeSpan LatchWindow = TimeSpan.FromMilliseconds(450);

    private sealed class State
    {
        public double Target;
        public bool Animating;
        public long LastWheelTicks = long.MinValue / 2;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    static SmoothScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((sv, e) =>
        {
            sv.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            if (e.NewValue is true)
                sv.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        });
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        var st = States.GetValue(sv, _ => new State());
        var now = Environment.TickCount64;
        var latched = now - st.LastWheelTicks < LatchWindow.TotalMilliseconds;

        // Not mid-gesture and the pointer rests on a fader that wants it: let the fader take it.
        if (!latched && FaderUnder(e.Source) is { WantsWheel: true })
            return;

        st.LastWheelTicks = now;
        var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (max <= 0) return; // nothing to scroll: leave the event alone

        var dy = e.Delta.Y;
        var precise = Math.Abs(dy) < 1 && dy != 0; // touchpad: fractional, high rate
        if (precise)
        {
            st.Animating = false;
            st.Target = Math.Clamp(sv.Offset.Y - dy * NotchPixels, 0, max);
            sv.Offset = new Vector(sv.Offset.X, st.Target);
        }
        else
        {
            var from = st.Animating ? st.Target : sv.Offset.Y;
            st.Target = Math.Clamp(from - dy * NotchPixels, 0, max);
            if (!st.Animating)
            {
                st.Animating = true;
                Step(sv, st);
            }
        }
        e.Handled = true;
    }

    private static void Step(ScrollViewer sv, State st)
    {
        if (!st.Animating) return;
        var top = TopLevel.GetTopLevel(sv);
        if (top is null)
        {
            sv.Offset = new Vector(sv.Offset.X, st.Target);
            st.Animating = false;
            return;
        }
        top.RequestAnimationFrame(_ =>
        {
            if (!st.Animating) return;
            var y = sv.Offset.Y;
            var next = y + (st.Target - y) * Ease;
            if (Math.Abs(st.Target - next) < 0.5)
            {
                next = st.Target;
                st.Animating = false;
            }
            sv.Offset = new Vector(sv.Offset.X, next);
            if (st.Animating) Step(sv, st);
        });
    }

    private static Fader? FaderUnder(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Fader f) return f;
            if (v is ScrollViewer) return null;
        }
        return null;
    }
}
