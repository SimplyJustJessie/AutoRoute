using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AutoRoute.App.Behaviors;

/// <summary>
/// Attached behavior that makes an output row accept dragged Sources. Bind
/// <see cref="DropCommandProperty"/> to the row's connect command; on drop the dragged Source's node
/// id is passed to it as the "connect Source → this output" intent. <see cref="AcceptsDropProperty"/>
/// lets a Hands-off row refuse drops, and <see cref="RejectNodeIdProperty"/> refuses the row's own
/// node.
/// <para>
/// The drag itself is run in-window by <see cref="DragSourceBehavior"/> (pointer capture + hit
/// testing), not through the platform's drag and drop: on X11, Avalonia 12 routes even an in-app drag
/// through XDND, which can't carry our payload, so every target read as "can't drop".
/// </para>
/// </summary>
public static class DropTargetBehavior
{
    public static readonly AttachedProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("DropCommand", typeof(DropTargetBehavior));

    public static readonly AttachedProperty<bool> AcceptsDropProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("AcceptsDrop", typeof(DropTargetBehavior), true);

    /// <summary>
    /// A source node this target refuses: the row's own node, so an output can never be fed its own
    /// sound (dragging an output's monitor back onto it would loop). -1 = none.
    /// </summary>
    public static readonly AttachedProperty<int> RejectNodeIdProperty =
        AvaloniaProperty.RegisterAttached<Control, int>("RejectNodeId", typeof(DropTargetBehavior), -1);

    /// <summary>True while an acceptable drag is hovering — for the drop-highlight style.</summary>
    public static readonly AttachedProperty<bool> IsDragOverProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsDragOver", typeof(DropTargetBehavior));

    public static void SetDropCommand(Control c, ICommand? v) => c.SetValue(DropCommandProperty, v);
    public static ICommand? GetDropCommand(Control c) => c.GetValue(DropCommandProperty);
    public static void SetAcceptsDrop(Control c, bool v) => c.SetValue(AcceptsDropProperty, v);
    public static bool GetAcceptsDrop(Control c) => c.GetValue(AcceptsDropProperty);
    public static void SetRejectNodeId(Control c, int v) => c.SetValue(RejectNodeIdProperty, v);
    public static int GetRejectNodeId(Control c) => c.GetValue(RejectNodeIdProperty);
    public static void SetIsDragOver(Control c, bool v) => c.SetValue(IsDragOverProperty, v);
    public static bool GetIsDragOver(Control c) => c.GetValue(IsDragOverProperty);

    // Exactly one highlighted target at a time.
    private static WeakReference<Control>? _current;

    private static void SetCurrent(Control? control)
    {
        if (_current is not null && _current.TryGetTarget(out var previous) && previous != control)
            SetIsDragOver(previous, false);
        _current = control is null ? null : new WeakReference<Control>(control);
        if (control is not null) SetIsDragOver(control, true);
    }

    /// <summary>Drop the highlight wherever it is (drag ended or left every target).</summary>
    public static void ClearCurrent() => SetCurrent(null);

    /// <summary>The drop target under <paramref name="point"/> (in <paramref name="top"/>'s space), if any.</summary>
    public static Control? TargetAt(TopLevel top, Point point)
    {
        for (var v = top.InputHitTest(point) as Visual; v is not null; v = v.GetVisualParent())
            if (v is Control c && GetDropCommand(c) is not null)
                return c;
        return null;
    }

    /// <summary>Would <paramref name="target"/> take <paramref name="nodeId"/>?</summary>
    public static bool Accepts(Control? target, int nodeId) =>
        target is not null && GetAcceptsDrop(target) && GetDropCommand(target) is { } cmd
        && nodeId != GetRejectNodeId(target) && cmd.CanExecute(nodeId);

    /// <summary>Hovering <paramref name="target"/>: highlight it if it takes the node. Returns that answer.</summary>
    public static bool Hover(Control? target, int nodeId)
    {
        var ok = Accepts(target, nodeId);
        SetCurrent(ok ? target : null);
        return ok;
    }

    /// <summary>Released over <paramref name="target"/>: connect if it takes the node.</summary>
    public static bool Drop(Control? target, int nodeId)
    {
        ClearCurrent();
        if (!Accepts(target, nodeId)) return false;
        GetDropCommand(target!)!.Execute(nodeId);
        return true;
    }
}
