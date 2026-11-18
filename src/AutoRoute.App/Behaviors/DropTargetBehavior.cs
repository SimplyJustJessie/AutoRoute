using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AutoRoute.App.Behaviors;

/// <summary>
/// Attached behavior that makes a Target Sink column accept dropped Sources. Bind
/// <see cref="DropCommandProperty"/> to the column's connect command; on drop the dragged Source
/// node id (see <see cref="DragSourceBehavior.SourceNodeFormat"/>) is passed to that command as the
/// "connect Source → this Target" intent. <see cref="AcceptsDropProperty"/> (bound to the column's
/// AcceptsDrop) lets a Protected column refuse drops.
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

    public static void SetRejectNodeId(Control c, int v) => c.SetValue(RejectNodeIdProperty, v);
    public static int GetRejectNodeId(Control c) => c.GetValue(RejectNodeIdProperty);

    /// <summary>True while a compatible drag is hovering — for a drop-highlight style trigger.</summary>
    public static readonly AttachedProperty<bool> IsDragOverProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsDragOver", typeof(DropTargetBehavior));

    public static void SetDropCommand(Control c, ICommand? v) => c.SetValue(DropCommandProperty, v);
    public static ICommand? GetDropCommand(Control c) => c.GetValue(DropCommandProperty);
    public static void SetAcceptsDrop(Control c, bool v) => c.SetValue(AcceptsDropProperty, v);
    public static bool GetAcceptsDrop(Control c) => c.GetValue(AcceptsDropProperty);
    public static void SetIsDragOver(Control c, bool v) => c.SetValue(IsDragOverProperty, v);
    public static bool GetIsDragOver(Control c) => c.GetValue(IsDragOverProperty);

    static DropTargetBehavior()
    {
        DropCommandProperty.Changed.AddClassHandler<Control>(OnDropCommandChanged);
    }

    private static void OnDropCommandChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        control.RemoveHandler(DragDrop.DragEnterEvent, OnDragOver);
        control.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        control.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        control.RemoveHandler(DragDrop.DropEvent, OnDrop);

        if (e.NewValue is ICommand)
        {
            DragDrop.SetAllowDrop(control, true);
            // DragEnter too: crossing from a row's padding onto its card raises DragEnter for the
            // card before the next DragOver; left unanswered, that one event had no effect and the
            // cursor flashed to the no-target hand for a pixel.
            control.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
            control.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            control.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            control.AddHandler(DragDrop.DropEvent, OnDrop);
        }
    }

    private static bool Compatible(Control control, DragEventArgs e) =>
        GetAcceptsDrop(control)
        && e.DataTransfer.TryGetValue(DragSourceBehavior.SourceNodeFormat) is { } src
        && src.NodeId != GetRejectNodeId(control);

    // Exactly one highlighted target at a time. Leave events can be missed on slow moves (and a
    // leave inside a row's own bounds is ignored on purpose), so highlighting a new target always
    // un-highlights the previous one, and the board clears it when the pointer is over no target.
    private static WeakReference<Control>? _current;

    private static void SetCurrent(Control? control)
    {
        if (_current is not null && _current.TryGetTarget(out var previous) && previous != control)
            SetIsDragOver(previous, false);
        _current = control is null ? null : new WeakReference<Control>(control);
        if (control is not null) SetIsDragOver(control, true);
    }

    /// <summary>Drop the highlight wherever it is (drag ended, or the pointer is over no target).</summary>
    public static void ClearCurrent() => SetCurrent(null);

    /// <summary>
    /// For a drag-aware container (the board root): a DragOver no target claimed means the pointer
    /// is over empty space — clear the highlight and show a steady "can't drop" cursor rather than
    /// whatever the platform picks for an unhandled effect.
    /// </summary>
    public static void OnUnclaimedDragOver(DragEventArgs e)
    {
        ClearCurrent();
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Control control) return;
        if (Compatible(control, e))
        {
            // Copy, not Link: X11 asks the cursor theme for "dnd-link", which Breeze doesn't ship,
            // so a Link drag fell back to the plain arrow. "dnd-copy" exists everywhere.
            e.DragEffects = DragDropEffects.Copy;
            SetCurrent(control);
        }
        else
        {
            ClearCurrent(); // no lingering highlight on the row we just came from
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private static void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is not Control control) return;
        // DragLeave bubbles up from children: crossing from the row onto its icon, fader or a chip
        // "leaves" the child and used to clear the highlight on every crossing (the flicker). Only
        // clear it when the pointer has actually left this control's bounds.
        var p = e.GetPosition(control);
        if (new Rect(control.Bounds.Size).Contains(p)) return;
        SetIsDragOver(control, false);
        if (_current is not null && _current.TryGetTarget(out var cur) && cur == control) _current = null;
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control control) return;
        ClearCurrent();
        if (!Compatible(control, e)) return;

        if (e.DataTransfer.TryGetValue(DragSourceBehavior.SourceNodeFormat) is { NodeId: var nodeId })
        {
            var command = GetDropCommand(control);
            if (command is not null && command.CanExecute(nodeId))
                command.Execute(nodeId);
        }
        e.Handled = true;
    }
}
