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
/// The floating card that follows the pointer during a Source drag. Avalonia's DragDrop renders no
/// native drag image, so without this the user gets zero feedback that they are holding anything.
/// The ghost lives in the TopLevel's <see cref="OverlayLayer"/> and is repositioned from DragOver
/// events (listened on the TopLevel with handledEventsToo, since columns handle their own DragOver);
/// the board root sets AllowDrop so those events keep firing between columns too.
/// </summary>
internal static class DragGhost
{
    private const double OffsetX = 16;
    private const double OffsetY = 14;

    private static Border? _ghost;
    private static OverlayLayer? _layer;
    private static TopLevel? _top;

    /// <summary>Show the ghost for a drag started on <paramref name="source"/>.</summary>
    public static void Show(Control source, string label, Point positionInLayer)
    {
        Hide();

        var top = TopLevel.GetTopLevel(source);
        var layer = OverlayLayer.GetOverlayLayer(source);
        if (top is null || layer is null) return;

        _top = top;
        _layer = layer;
        _ghost = Build(source, label);
        layer.Children.Add(_ghost);
        MoveTo(positionInLayer);

        // Columns mark their DragOver handled, so listen with handledEventsToo to keep tracking.
        top.AddHandler(DragDrop.DragOverEvent, OnTopLevelDragOver,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public static void Hide()
    {
        _top?.RemoveHandler(DragDrop.DragOverEvent, OnTopLevelDragOver);
        if (_layer is not null && _ghost is not null) _layer.Children.Remove(_ghost);
        _top = null;
        _layer = null;
        _ghost = null;
    }

    private static void OnTopLevelDragOver(object? sender, DragEventArgs e)
    {
        if (_layer is not null) MoveTo(e.GetPosition(_layer));
        // Not over a Target Sink column (nothing claimed the event): show a no-drop cursor
        // instead of the misleading default copy/link one.
        if (!e.Handled)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private static void MoveTo(Point p)
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
