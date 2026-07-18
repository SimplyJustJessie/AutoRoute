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
    /// <summary>Data-object format key for the dragged Source node id (boxed int).</summary>
    public const string SourceNodeFormat = "autoroute/source-node-id";

    private const double DragThreshold = 4.0;

    public static readonly AttachedProperty<int?> SourceNodeIdProperty =
        AvaloniaProperty.RegisterAttached<Control, int?>("SourceNodeId", typeof(DragSourceBehavior));

    public static void SetSourceNodeId(Control control, int? value) =>
        control.SetValue(SourceNodeIdProperty, value);

    public static int? GetSourceNodeId(Control control) =>
        control.GetValue(SourceNodeIdProperty);

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
        State.AddOrUpdate(control, new PressState { Origin = e.GetPosition(control), Armed = true });
    }

    private static async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control) return;
        if (!State.TryGetValue(control, out var st) || !st.Armed) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;

        var delta = e.GetPosition(control) - st.Origin;
        if (System.Math.Abs(delta.X) < DragThreshold && System.Math.Abs(delta.Y) < DragThreshold) return;

        var nodeId = GetSourceNodeId(control);
        if (nodeId is null) return;

        st.Armed = false; // one drag per press
        // Classic DragDrop API: deprecated in Avalonia 11.3 in favour of DataTransfer, but stable
        // and fully functional. Kept intentionally; Wave 3 may migrate to DoDragDropAsync/DataTransfer.
#pragma warning disable CS0618
        var data = new DataObject();
        data.Set(SourceNodeFormat, nodeId.Value);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Link | DragDropEffects.Copy);
#pragma warning restore CS0618
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
    }
}
