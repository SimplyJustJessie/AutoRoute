using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoRoute.App.Behaviors;

/// <summary>
/// The floating card that follows the pointer during a Source drag, so the user can see what they
/// are holding. It lives in the TopLevel's <see cref="OverlayLayer"/>; <see cref="DragSourceBehavior"/>
/// moves it on every pointer move of the drag.
/// </summary>
internal static class DragGhost
{
    private const double OffsetX = 16;
    private const double OffsetY = 14;

    private static Border? _ghost;
    private static OverlayLayer? _layer;

    /// <summary>Show the ghost for a drag started on <paramref name="source"/>.</summary>
    public static void Show(Control source, string label, Point positionInLayer)
    {
        Hide();

        var top = TopLevel.GetTopLevel(source);
        var layer = OverlayLayer.GetOverlayLayer(source);
        if (top is null || layer is null) return;

        _layer = layer;
        _ghost = Build(source, label);
        layer.Children.Add(_ghost);
        Move(positionInLayer);
    }

    public static void Hide()
    {
        if (_layer is not null && _ghost is not null) _layer.Children.Remove(_ghost);
        _layer = null;
        _ghost = null;
    }

    /// <summary>Put the ghost by the pointer (position in the overlay layer).</summary>
    public static void Move(Point p)
    {
        if (_ghost is null) return;
        Canvas.SetLeft(_ghost, p.X + OffsetX);
        Canvas.SetTop(_ghost, p.Y + OffsetY);
    }

    private static Border Build(Control source, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        if (source.FindResource("IconWave") is StreamGeometry wave)
        {
            content.Children.Add(new Path
            {
                Data = wave,
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            // The ghost floats in the overlay with an infinite measure — cap it so a real-world
            // device name doesn't produce a screen-wide ghost.
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return new Border
        {
            Classes = { "dragGhost" },
            IsHitTestVisible = false,
            Child = content,
        };
    }
}
