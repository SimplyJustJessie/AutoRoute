using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AutoRoute.App.Controls;

/// <summary>
/// A line icon drawn from a 24×24 design grid (the glyph geometries in App.axaml). Unlike a
/// stretched <see cref="Avalonia.Controls.Shapes.Path"/>, every icon gets the same scale (no glyph
/// blown up to fill its own bounds), sits centred on whole pixels, and keeps its stroke at
/// <see cref="StrokeThickness"/> real pixels at any size. <see cref="FillData"/> is drawn filled
/// first, for icons with a solid body (the in/out badges), then <see cref="Data"/> is stroked.
/// </summary>
public sealed class GlyphIcon : Control
{
    private const double Grid = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<GlyphIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<Geometry?> FillDataProperty =
        AvaloniaProperty.Register<GlyphIcon, Geometry?>(nameof(FillData));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<GlyphIcon, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<GlyphIcon, double>(nameof(StrokeThickness), 1.6);

    static GlyphIcon()
    {
        AffectsRender<GlyphIcon>(DataProperty, FillDataProperty, ForegroundProperty, StrokeThicknessProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Geometry? FillData { get => GetValue(FillDataProperty); set => SetValue(FillDataProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        if (Foreground is not { } brush) return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var scale = size / Grid;
        // Centre on whole pixels so strokes land the same way in every tile.
        var x = Math.Round((Bounds.Width - size) / 2);
        var y = Math.Round((Bounds.Height - size) / 2);

        using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y)))
        {
            if (FillData is { } fill) ctx.DrawGeometry(brush, null, fill);
            if (Data is { } data)
                ctx.DrawGeometry(null, new Pen(brush, StrokeThickness / scale,
                    lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
        }
    }
}
