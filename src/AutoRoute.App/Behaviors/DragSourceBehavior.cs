using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AutoRoute.App.Behaviors;

/// <summary>
/// Attached behavior that makes a palette card a drag source. Set
/// <see cref="SourceNodeIdProperty"/> to the Source's live node id; a press-and-drag then starts an
/// Avalonia <see cref="DragDrop"/> operation whose payload (<see cref="SourceNodeFormat"/>) carries
/// that id. The card is not consumed — it stays in the palette, enabling fan-out.
/// </summary>
public static class DragSourceBehavior
{
    /// <summary>The dragged Source node id, as an in-process drag payload.</summary>
    public static readonly DataFormat<SourceNode> SourceNodeFormat =
        DataFormat.CreateInProcessFormat<SourceNode>("autoroute-source-node");

    /// <summary>Drag payload: the live node id of the Source being dragged.</summary>
    public sealed record SourceNode(int NodeId);

    /// <summary>
    /// The Source being dragged right now, if this app started the drag. Drop targets read it while
    /// hovering: on X11 a drag runs through the XDND protocol even inside one app, and the payload's
    /// value isn't readable until the drop, so hovering rows answered "can't drop".
    /// </summary>
    public static SourceNode? Current { get; private set; }

    private const double DragThreshold = 4.0;

    public static readonly AttachedProperty<int?> SourceNodeIdProperty =
        AvaloniaProperty.RegisterAttached<Control, int?>("SourceNodeId", typeof(DragSourceBehavior));

    /// <summary>Label shown on the floating drag ghost (bind to the Source's title).</summary>
    public static readonly AttachedProperty<string?> DragLabelProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("DragLabel", typeof(DragSourceBehavior));

    public static void SetSourceNodeId(Control control, int? value) =>
        control.SetValue(SourceNodeIdProperty, value);

    public static int? GetSourceNodeId(Control control) =>
        control.GetValue(SourceNodeIdProperty);

    public static void SetDragLabel(Control control, string? value) =>
        control.SetValue(DragLabelProperty, value);

    public static string? GetDragLabel(Control control) =>
        control.GetValue(DragLabelProperty);

    private static readonly ConditionalWeakTable<Control, PressState> State = new();

    static DragSourceBehavior()
    {
        SourceNodeIdProperty.Changed.AddClassHandler<Control>(OnSourceNodeIdChanged);
    }

    private static void OnSourceNodeIdChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        // Detach first (idempotent), then attach if we now have a node id.
        control.PointerPressed -= OnPointerPressed;
        control.PointerMoved -= OnPointerMoved;
        control.PointerReleased -= OnPointerReleased;

        if (e.NewValue is int)
        {
            control.PointerPressed += OnPointerPressed;
            control.PointerMoved += OnPointerMoved;
            control.PointerReleased += OnPointerReleased;
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        State.AddOrUpdate(control, new PressState { Origin = e.GetPosition(control), Armed = true, Press = e });
    }

    private static async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control) return;
        if (!State.TryGetValue(control, out var st) || !st.Armed) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;

        var delta = e.GetPosition(control) - st.Origin;
        if (System.Math.Abs(delta.X) < DragThreshold && System.Math.Abs(delta.Y) < DragThreshold) return;

        var nodeId = GetSourceNodeId(control);
        if (nodeId is null || st.Press is not { } press) return;

        st.Armed = false; // one drag per press

        // Lift feedback: dim the palette entry and float a ghost card under the pointer for the
        // whole drag (Avalonia renders no native drag image).
        var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(control);
        control.Classes.Add("dragging");
        // While a drag is in flight, row internals stop hit-testing (BoardStyles) so the drop
        // target is always the row itself: no hover flicker on buttons, no enter/leave churn.
        var root = TopLevel.GetTopLevel(control);
        root?.Classes.Add("dragActive");
        DragGhost.Show(control, GetDragLabel(control) ?? "Source",
            layer is null ? default : e.GetPosition(layer));

        // The drag starts once the pointer has moved past the threshold, but Avalonia wants the
        // press that began it.
        var payload = new SourceNode(nodeId.Value);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(SourceNodeFormat, payload));
        Current = payload;
        try
        {
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        finally
        {
            DragGhost.Hide();
            control.Classes.Remove("dragging");
            root?.Classes.Remove("dragActive");
            DropTargetBehavior.ClearCurrent();
            st.Press = null;
            Current = null;
        }
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control control && State.TryGetValue(control, out var st))
            st.Armed = false;
    }

    private sealed class PressState
    {
        public Point Origin;
        public bool Armed;
        public PointerPressedEventArgs? Press;
    }
}
