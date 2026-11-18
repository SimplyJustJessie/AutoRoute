using System;
using System.Globalization;
using AutoRoute.App.Services;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Controls;
using Avalonia.Media;

namespace AutoRoute.App.Views;

/// <summary><see cref="NodeGlyph"/> → its outline geometry (<c>Glyph*</c> resources in App.axaml).</summary>
public sealed class GlyphConverter : IValueConverter
{
    public static readonly GlyphConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not NodeGlyph glyph || Application.Current is not { } app) return null;
        return app.TryFindResource("Glyph" + glyph, out var res) && res is Geometry g ? g : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
