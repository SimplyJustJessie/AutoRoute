using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AutoRoute.App.Controls;

/// <summary>
/// The green "playing" equaliser: three bars driven by the live level after volume, so turning an
/// app down visibly shrinks them.
/// <para>
/// It's an activity indicator, not a precise meter (the fader fill is that), so it favours
/// visible motion. The level is read on a dB scale (-60..-6 dB), so quiet audio still shows but louder audio is clearly taller, each bar wanders
/// towards its own random target so the three never move in lockstep, and sudden jumps in level
/// (beats, consonants) kick the bars by different amounts.
/// </para>
/// <para>
/// Without a live feed (<see cref="IsLive"/> false) it draws the static icon shape. It only redraws
/// when <see cref="Level"/> changes, which only happens while the meter feed runs.
/// </para>
/// </summary>
public sealed class LevelBars : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelBars, double>(nameof(Level));

    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<LevelBars, double>(nameof(Volume), 1);

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<LevelBars, bool>(nameof(IsMuted));

    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<LevelBars, bool>(nameof(IsLive));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<LevelBars, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> BarThicknessProperty =
        AvaloniaProperty.Register<LevelBars, double>(nameof(BarThickness), 3);

    private const int Bars = 3;
    private static readonly double[] StaticHeights = { 0.5, 1.0, 0.7 }; // short, tall, middle
    private static readonly double[] Kick = { 0.6, -0.4, 0.8 };         // transient shake per bar
    private const double Floor = 0.2;       // the shortest a bar gets while shown
    private const double QuietDb = -60;     // bottom of the scale
    private const double LoudDb = -6;       // top: loud music peaks around here

    private static int _seed = 17;
    private readonly Random _rng = new(System.Threading.Interlocked.Increment(ref _seed) * 7919);
    private readonly double[] _mult = { 0.6, 0.9, 0.75 };
    private readonly double[] _target = { 0.6, 0.9, 0.75 };
    private readonly int[] _hold = new int[Bars];
    private readonly double[] _goal = new double[Bars];   // set ~30×/s from the meter
    private readonly double[] _height = new double[Bars]; // eased towards _goal every display frame
    private double _slow;
    private TimeSpan? _lastFrame;
    private bool _animating;
    private IPen? _pen;

    static LevelBars()
    {
        AffectsRender<LevelBars>(IsLiveProperty, IsMutedProperty, VolumeProperty, ForegroundProperty, BarThicknessProperty);
    }

    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Volume { get => GetValue(VolumeProperty); set => SetValue(VolumeProperty, value); }
    public bool IsMuted { get => GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }
    public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double BarThickness { get => GetValue(BarThicknessProperty); set => SetValue(BarThicknessProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelProperty)
        {
            Step(Loudness(Out()));
            Animate();
        }
        else if (change.Property == ForegroundProperty || change.Property == BarThicknessProperty)
        {
            _pen = null;
        }
        else if (change.Property == IsVisibleProperty && IsVisible)
        {
            Array.Clear(_height); // appear from the bottom, never at full height
        }
        else if (change.Property == IsLiveProperty && !IsLive)
        {
            Array.Clear(_goal);
            Array.Clear(_height);
            _slow = 0;
        }
    }

    /// <summary>What actually comes out: the level after this entry's volume and mute (fader units).</summary>
    private double Out() => IsMuted ? 0 : Math.Clamp(Level, 0, 1) * Math.Clamp(Volume, 0, 1);

    /// <summary>
    /// Fader units are the cube root of amplitude; map that linearly onto -60..-6 dB. Quiet speech
    /// (about -44 dB) lands near a third, a normal video (-24 dB) two thirds.
    /// </summary>
    public static double Loudness(double faderUnits)
    {
        var amplitude = faderUnits * faderUnits * faderUnits;
        if (amplitude <= 1e-6) return 0;
        var db = 20 * Math.Log10(amplitude);
        return Math.Clamp((db - QuietDb) / (LoudDb - QuietDb), 0, 1);
    }

    /// <summary>One frame: advance each bar's wander and transient kick, then settle its height.</summary>
    private void Step(double loud)
    {
        if (loud <= 0)
        {
            Array.Clear(_goal);
            _slow = 0;
            return;
        }

        if (_slow <= 0) _slow = loud; // starting up: no fake burst from silence
        var transient = Math.Max(0, loud - _slow); // how much louder than the recent average
        _slow += (loud - _slow) * 0.15;

        // A jump in level shakes the bars up; it never makes them taller than the level allows.
        var shake = Math.Min(1, transient * 3);
        for (var i = 0; i < Bars; i++)
        {
            if (--_hold[i] <= 0)
            {
                _target[i] = _rng.NextDouble();
                _hold[i] = 2 + _rng.Next(4); // a new target every 70-170 ms
            }
            _mult[i] += (_target[i] - _mult[i]) * 0.5;

            var r = Math.Clamp(_mult[i] + shake * Kick[i], 0, 1);
            // The loudest bar reaches the level; the others sit anywhere down to a quarter of it, so
            // the shape keeps changing even when loud music pins the level near the top.
            _goal[i] = loud * (0.25 + 0.75 * r);
        }
    }

    /// <summary>
    /// Glide the bars towards their goals at the display's frame rate, so 30 meter updates a second
    /// read as smooth motion. Rises are quick (~45 ms), falls slower (~140 ms). Runs only until the
    /// bars settle, and only while attached, so a hidden or silent entry costs nothing.
    /// </summary>
    private void Animate()
    {
        if (_animating) return;
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top)
        {
            Array.Clear(_height); // hidden: when it shows, the bars grow in from the bottom
            return;
        }
        _animating = true;
        _lastFrame = null;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 1 / 60.0;
        _lastFrame = now;

        var moving = false;
        for (var i = 0; i < Bars; i++)
        {
            var diff = _goal[i] - _height[i];
            var tau = diff > 0 ? 0.045 : 0.14;
            _height[i] += diff * (1 - Math.Exp(-dt / tau));
            if (Math.Abs(_goal[i] - _height[i]) > 0.003) moving = true;
            else _height[i] = _goal[i];
        }
        InvalidateVisual();

        if (moving && IsLive && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else _animating = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(14, 13);

    public override void Render(DrawingContext ctx)
    {
        if (Foreground is null) return;
        var t = BarThickness;
        _pen ??= new Pen(Foreground, t, lineCap: PenLineCap.Round);

        var w = Bounds.Width;
        var h = Bounds.Height;
        var bottom = h - t / 2;
        var span = bottom - t / 2;     // round caps poke out half a thickness at each end
        var step = (w - t) / (Bars - 1);

        for (var i = 0; i < Bars; i++)
        {
            var frac = IsLive ? Floor + (1 - Floor) * _height[i] : StaticHeights[i];
            var x = t / 2 + step * i;
            ctx.DrawLine(_pen, new Point(x, bottom), new Point(x, bottom - span * frac + 0.01));
        }
    }
}
