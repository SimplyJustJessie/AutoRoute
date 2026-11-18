using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AutoRoute.App.Controls;

/// <summary>
/// The initials on an app tile, centred by the letters' actual ink rather than the text box. A
/// TextBlock centres its line box (side bearings, ascender and descender included), which leaves
/// capitals sitting visibly off-centre in a small tile. This builds the text's outline, centres
/// its bounds on whole pixels, and fills it.
/// </summary>
public sealed class TileLetter : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TileLetter, string?>(nameof(Text));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TileLetter, double>(nameof(FontSize), 14);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<TileLetter, IBrush?>(nameof(Foreground), Brushes.White);

    private Geometry? _shape;

    static TileLetter()
    {
        AffectsRender<TileLetter>(TextProperty, FontSizeProperty, ForegroundProperty);
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FontSizeProperty)
            _shape = null;
    }

    public override void Render(DrawingContext ctx)
    {
        if (string.IsNullOrEmpty(Text) || Foreground is not { } brush) return;
        _shape ??= new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Bold),
                FontSize, brush)
            .BuildGeometry(default);
        if (_shape is null) return;

        var ink = _shape.Bounds;
        var dx = Math.Round((Bounds.Width - ink.Width) / 2 - ink.X);
        var dy = Math.Round((Bounds.Height - ink.Height) / 2 - ink.Y);
        using (ctx.PushTransform(Matrix.CreateTranslation(dx, dy)))
            ctx.DrawGeometry(brush, null, _shape);
    }
}
