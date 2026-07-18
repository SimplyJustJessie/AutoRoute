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
        control.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        control.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        control.RemoveHandler(DragDrop.DropEvent, OnDrop);

        if (e.NewValue is ICommand)
        {
            DragDrop.SetAllowDrop(control, true);
            control.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            control.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            control.AddHandler(DragDrop.DropEvent, OnDrop);
        }
    }

    // Classic DragDrop API (e.Data): deprecated in Avalonia 11.3 in favour of DataTransfer, but
    // stable and functional. Kept intentionally; Wave 3 may migrate to the DataTransfer API.
#pragma warning disable CS0618
    private static bool Compatible(Control control, DragEventArgs e) =>
        GetAcceptsDrop(control) && e.Data.Contains(DragSourceBehavior.SourceNodeFormat);

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Control control) return;
        if (Compatible(control, e))
        {
            e.DragEffects = DragDropEffects.Link;
            SetIsDragOver(control, true);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private static void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Control control) SetIsDragOver(control, false);
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control control) return;
        SetIsDragOver(control, false);
        if (!Compatible(control, e)) return;

        if (e.Data.Get(DragSourceBehavior.SourceNodeFormat) is int nodeId)
        {
            var command = GetDropCommand(control);
            if (command is not null && command.CanExecute(nodeId))
                command.Execute(nodeId);
        }
        e.Handled = true;
    }
#pragma warning restore CS0618
}
