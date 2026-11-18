using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AutoRoute.App.Behaviors;

/// <summary>
/// Attached behavior that makes a Source entry draggable onto output rows. Set
/// <see cref="SourceNodeIdProperty"/> to the Source's live node id; press and move past a few pixels
/// to pick it up, release over a row to connect it there. The entry stays in place (fan-out).
/// <para>
/// The drag runs entirely inside the window: the entry captures the pointer, the row under it is
/// found by hit testing (<see cref="DropTargetBehavior.TargetAt"/>), and the cursor is set here —
/// copy over a row that takes the Source, a no-entry sign anywhere else. No platform drag and drop:
/// on X11, Avalonia 12 routes even an in-app drag through XDND, which can't carry our payload, so
/// every row answered "can't drop" from the first pixel. Escape or losing the pointer cancels.
/// </para>
/// </summary>
public static class DragSourceBehavior
{
    private const double DragThreshold = 4.0;

    internal static readonly Cursor CopyCursor = new(StandardCursorType.DragCopy);
    internal static readonly Cursor NoCursor = new(StandardCursorType.No);

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

    /// <summary>True while a Source drag is in progress (for tests and diagnostics).</summary>
    public static bool IsDragging => _active is not null;

    private static readonly ConditionalWeakTable<Control, PressState> State = new();
    private static Drag? _active;

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
        control.PointerCaptureLost -= OnCaptureLost;

        if (e.NewValue is int)
        {
            control.PointerPressed += OnPointerPressed;
            control.PointerMoved += OnPointerMoved;
            control.PointerReleased += OnPointerReleased;
            control.PointerCaptureLost += OnCaptureLost;
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        State.AddOrUpdate(control, new PressState { Origin = e.GetPosition(control), Armed = true });
    }

    private static void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control) return;

        if (_active is { } drag && drag.Source == control)
        {
            Update(drag, e);
            e.Handled = true;
            return;
        }

        if (!State.TryGetValue(control, out var st) || !st.Armed) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            st.Armed = false;
            return;
        }

        var delta = e.GetPosition(control) - st.Origin;
        if (System.Math.Abs(delta.X) < DragThreshold && System.Math.Abs(delta.Y) < DragThreshold) return;
        st.Armed = false; // one drag per press

        if (GetSourceNodeId(control) is not { } nodeId || TopLevel.GetTopLevel(control) is not { } top) return;
        Begin(control, top, nodeId, e);
        e.Handled = true;
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control) return;
        if (State.TryGetValue(control, out var st)) st.Armed = false;
        if (_active is not { } drag || drag.Source != control) return;

        var target = DropTargetBehavior.TargetAt(drag.Top, e.GetPosition(drag.Top));
        End(drag, e.Pointer);
        DropTargetBehavior.Drop(target, drag.NodeId);
        e.Handled = true;
    }

    // Capture taken away (window lost focus, another grab): cancel without connecting.
    private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_active is { } drag && drag.Source == sender) End(drag, pointer: null);
    }

    private static void OnTopKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _active is not { } drag) return;
        End(drag, drag.Pointer);
        e.Handled = true;
    }

    private static void Begin(Control source, TopLevel top, int nodeId, PointerEventArgs e)
    {
        var drag = new Drag(source, top, nodeId, e.Pointer);
        _active = drag;
        e.Pointer.Capture(source);

        // Lift feedback: dim the entry and float a ghost card under the pointer for the whole
        // drag. Row internals stop hit-testing (BoardStyles) so the target is always the row.
        source.Classes.Add("dragging");
        top.Classes.Add("dragActive");
        var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(source);
        DragGhost.Show(source, GetDragLabel(source) ?? "Source", layer is null ? default : e.GetPosition(layer));
        top.AddHandler(InputElement.KeyDownEvent, OnTopKeyDown, RoutingStrategies.Tunnel);

        Update(drag, e);
    }

    private static void Update(Drag drag, PointerEventArgs e)
    {
        if (Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(drag.Source) is { } layer)
            DragGhost.Move(e.GetPosition(layer));
        var target = DropTargetBehavior.TargetAt(drag.Top, e.GetPosition(drag.Top));
        var ok = DropTargetBehavior.Hover(target, drag.NodeId);
        // On the grabbed entry (it holds the capture) and on the window, so whichever element the
        // platform asks for the cursor answers the same.
        var cursor = ok ? CopyCursor : NoCursor;
        drag.Source.Cursor = cursor;
        drag.Top.Cursor = cursor;
    }

    private static void End(Drag drag, IPointer? pointer)
    {
        if (_active != drag) return;
        _active = null;
        drag.Top.RemoveHandler(InputElement.KeyDownEvent, OnTopKeyDown);
        DragGhost.Hide();
        drag.Source.Classes.Remove("dragging");
        drag.Top.Classes.Remove("dragActive");
        drag.Source.ClearValue(InputElement.CursorProperty);
        drag.Top.ClearValue(InputElement.CursorProperty);
        DropTargetBehavior.ClearCurrent();
        if (pointer?.Captured == drag.Source) pointer.Capture(null);
    }

    private sealed record Drag(Control Source, TopLevel Top, int NodeId, IPointer Pointer);

    private sealed class PressState
    {
        public Point Origin;
        public bool Armed;
    }
}
