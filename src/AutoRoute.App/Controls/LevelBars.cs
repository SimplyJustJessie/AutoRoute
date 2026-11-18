using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AutoRoute.App.Controls;

/// <summary>
/// The green "playing" equaliser: four bars that follow the live level after volume, so turning an
/// app down visibly shrinks them. Each bar reads the level a frame or two later than its neighbour,
/// which makes them ripple rather than pump in lockstep. Without a live feed
/// (<see cref="IsLive"/> false) it draws the static icon shape. It only redraws when
/// <see cref="Level"/> changes, which only happens while the meter feed runs.
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
        AvaloniaProperty.Register<LevelBars, double>(nameof(BarThickness), 2.2);

    // The static icon: bars 7, 15, 10 and 5 units tall out of 15.
    private static readonly double[] StaticHeights = { 7 / 15.0, 1, 10 / 15.0, 5 / 15.0 };
    // Per bar: how many frames behind it reads, and how tall it gets at full level.
    private static readonly int[] Lag = { 1, 0, 2, 3 };
    private static readonly double[] Weight = { 0.78, 1.0, 0.9, 0.66 };
    private const double Floor = 0.18; // a running but silent stream still shows a little

    private readonly double[] _history = new double[4];
    private int _head;
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
            var v = Out();
            if (v <= 0) Array.Clear(_history); // settled: no lagging bar left hanging up
            _head = (_head + 1) % _history.Length;
            _history[_head] = v;
            InvalidateVisual();
        }
        else if (change.Property == ForegroundProperty || change.Property == BarThicknessProperty)
        {
            _pen = null;
        }
        else if (change.Property == IsLiveProperty && !IsLive)
        {
            Array.Clear(_history);
        }
    }

    /// <summary>What actually comes out: the level after this entry's volume and mute.</summary>
    private double Out() => IsMuted ? 0 : Math.Clamp(Level, 0, 1) * Math.Clamp(Volume, 0, 1);

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
        var step = (w - t) / 3;

        for (var i = 0; i < 4; i++)
        {
            double frac;
            if (!IsLive) frac = StaticHeights[i];
            else
            {
                var v = _history[(_head - Lag[i] + _history.Length) % _history.Length] * Weight[i];
                frac = Floor + (1 - Floor) * Math.Clamp(v, 0, 1);
            }
            var x = t / 2 + step * i;
            ctx.DrawLine(_pen, new Point(x, bottom), new Point(x, bottom - span * frac + 0.01));
        }
    }
}
