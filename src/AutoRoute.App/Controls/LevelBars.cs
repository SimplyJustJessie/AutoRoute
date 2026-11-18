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
/// visible motion. The level is read on a dB scale that lifts quiet audio, each bar wanders
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
    private static readonly double[] Kick = { 1.0, 0.6, 1.3 };          // transient response per bar
    private const double Floor = 0.2;       // running but silent: three dots
    private const double QuietDb = -60;     // bottom of the scale
    private const double LoudDb = -6;       // top: loud music peaks around here

    private static int _seed = 17;
    private readonly Random _rng = new(System.Threading.Interlocked.Increment(ref _seed) * 7919);
    private readonly double[] _mult = { 0.6, 0.9, 0.75 };
    private readonly double[] _target = { 0.6, 0.9, 0.75 };
    private readonly int[] _hold = new int[Bars];
    private readonly double[] _height = new double[Bars];
    private double _slow;
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
            InvalidateVisual();
        }
        else if (change.Property == ForegroundProperty || change.Property == BarThicknessProperty)
        {
            _pen = null;
        }
        else if (change.Property == IsLiveProperty && !IsLive)
        {
            Array.Clear(_height);
            _slow = 0;
        }
    }

    /// <summary>What actually comes out: the level after this entry's volume and mute (fader units).</summary>
    private double Out() => IsMuted ? 0 : Math.Clamp(Level, 0, 1) * Math.Clamp(Volume, 0, 1);

    /// <summary>
    /// Fader units are the cube root of amplitude; map that onto -60..-6 dB, then lift it again
    /// (square root) so a quiet video still reaches half height.
    /// </summary>
    public static double Loudness(double faderUnits)
    {
        var amplitude = faderUnits * faderUnits * faderUnits;
        if (amplitude <= 1e-6) return 0;
        var db = 20 * Math.Log10(amplitude);
        return Math.Sqrt(Math.Clamp((db - QuietDb) / (LoudDb - QuietDb), 0, 1));
    }

    /// <summary>One frame: advance each bar's wander and transient kick, then settle its height.</summary>
    private void Step(double loud)
    {
        if (loud <= 0)
        {
            Array.Clear(_height);
            _slow = 0;
            return;
        }

        if (_slow <= 0) _slow = loud; // starting up: no fake burst from silence
        var transient = Math.Max(0, loud - _slow); // how much louder than the recent average
        _slow += (loud - _slow) * 0.15;

        for (var i = 0; i < Bars; i++)
        {
            if (--_hold[i] <= 0)
            {
                _target[i] = 0.3 + 0.7 * _rng.NextDouble();
                _hold[i] = 2 + _rng.Next(5); // a new target every 70-200 ms
            }
            _mult[i] += (_target[i] - _mult[i]) * 0.45;

            var want = Math.Clamp(loud * _mult[i] + transient * Kick[i], 0, 1);
            // Up fast, down a little slower, so peaks read as jumps rather than flicker.
            _height[i] = want > _height[i] ? want : _height[i] + (want - _height[i]) * 0.5;
        }
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
