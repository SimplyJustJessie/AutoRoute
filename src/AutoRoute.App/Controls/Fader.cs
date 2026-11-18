using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace AutoRoute.App.Controls;

/// <summary>
/// A thin volume fader that doubles as a level meter (OBS-mixer style): a 6 px track, a
/// green post-fader level fill behind the knob, and the region past the knob dimmed. Drag or
/// click to set, mouse wheel and arrow keys to nudge. <see cref="Level"/> is for the live meter
/// (0..1, post-fader); while it is 0 the fader is simply a volume slider.
/// </summary>
public sealed class Fader : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Fader, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => Math.Clamp(double.IsNaN(v) ? 0 : v, 0, 1));

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<Fader, double>(nameof(Level));

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<Fader, bool>(nameof(IsMuted));

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#2A2C33"));
    private static readonly IBrush Rest = new SolidColorBrush(Color.Parse("#1C1D22"));
    private static readonly IBrush LevelBrush = new SolidColorBrush(Color.Parse("#5BC98F"));
    private static readonly IBrush MutedLevel = new SolidColorBrush(Color.Parse("#3A3D47"));
    private static readonly IBrush Knob = new SolidColorBrush(Color.Parse("#ECEDEF"));
    private static readonly IBrush KnobMuted = new SolidColorBrush(Color.Parse("#8A8D97"));
    private static readonly IPen FocusPen = new Pen(new SolidColorBrush(Color.Parse("#7C9AFF")), 2);

    private const double TrackHeight = 6;
    private const double KnobRadius = 7;

    private bool _dragging;

    // The drawn level glides towards Level on display frames, so small wobbles in loudness don't
    // make the fill vibrate: up in ~80 ms, down in ~300 ms.
    private double _shownLevel;
    private TimeSpan? _lastFrame;
    private bool _animating;
    private const double RiseSeconds = 0.08;
    private const double FallSeconds = 0.3;
    private long _hoverSince = long.MaxValue;

    /// <summary>
    /// The pointer has rested on the fader long enough that a wheel turn is meant for it, not a
    /// list scroll passing over it (see <see cref="Behaviors.SmoothScroll"/>).
    /// </summary>
    public bool WantsWheel => IsPointerOver && Environment.TickCount64 - _hoverSince >= 350;

    static Fader()
    {
        AffectsRender<Fader>(ValueProperty, IsMutedProperty);
        FocusableProperty.OverrideDefaultValue<Fader>(true);
        CursorProperty.OverrideDefaultValue<Fader>(new Cursor(StandardCursorType.Hand));
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsMuted { get => GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }

    // A 28 px tall hit area around a 6 px track: easy to grab, small to look at.
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 140 : availableSize.Width, 28);

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var usable = Math.Max(1, w - 2 * KnobRadius);
        var y = (h - TrackHeight) / 2;
        var x0 = KnobRadius;
        var knobX = x0 + usable * Value;

        ctx.DrawRectangle(Track, null, new RoundedRect(new Rect(x0, y, usable, TrackHeight), TrackHeight / 2));
        if (knobX < x0 + usable)
            ctx.DrawRectangle(Rest, null, new RoundedRect(new Rect(knobX, y, x0 + usable - knobX, TrackHeight), 0, TrackHeight / 2, TrackHeight / 2, 0));

        var level = Math.Clamp(_shownLevel, 0, 1) * Value;
        if (level > 0.001)
            ctx.DrawRectangle(IsMuted ? MutedLevel : LevelBrush, null,
                new RoundedRect(new Rect(x0, y, usable * level, TrackHeight), TrackHeight / 2));

        var center = new Point(knobX, h / 2);
        if (IsFocused)
            ctx.DrawEllipse(null, FocusPen, center, KnobRadius + 2.5, KnobRadius + 2.5);
        ctx.DrawEllipse(IsMuted ? KnobMuted : Knob, null, center, KnobRadius, KnobRadius);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelProperty) AnimateLevel();
    }

    private void AnimateLevel()
    {
        if (_animating) return;
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top)
        {
            _shownLevel = Level;
            InvalidateVisual();
            return;
        }
        _animating = true;
        _lastFrame = null;
        top.RequestAnimationFrame(OnLevelFrame);
    }

    private void OnLevelFrame(TimeSpan now)
    {
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 1 / 60.0;
        _lastFrame = now;
        var target = Math.Clamp(Level, 0, 1);
        var diff = target - _shownLevel;
        _shownLevel += diff * (1 - Math.Exp(-dt / (diff > 0 ? RiseSeconds : FallSeconds)));
        var settled = Math.Abs(target - _shownLevel) < 0.002;
        if (settled) _shownLevel = target;
        InvalidateVisual();

        if (!settled && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnLevelFrame);
        else _animating = false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        SetFromX(e.GetPosition(this).X);
        Focus();
        e.Handled = true; // never let a fader press start a card drag or a window move
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        SetFromX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hoverSince = Environment.TickCount64;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverSince = long.MaxValue;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!WantsWheel) return; // just passing over: let the list scroll
        Value = Math.Round(Value + Math.Sign(e.Delta.Y) * 0.02, 2);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.Key switch
        {
            Key.Left or Key.Down => -0.05,
            Key.Right or Key.Up => 0.05,
            Key.Home => -1.0,
            Key.End => 1.0,
            _ => 0.0,
        };
        if (step == 0) return;
        Value = Math.Round(Value + step, 2);
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
    protected override void OnLostFocus(FocusChangedEventArgs e) { base.OnLostFocus(e); InvalidateVisual(); }

    private void SetFromX(double x)
    {
        var usable = Math.Max(1, Bounds.Width - 2 * KnobRadius);
        Value = Math.Round((x - KnobRadius) / usable, 3);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ControlAutomationPeer(this);
}
