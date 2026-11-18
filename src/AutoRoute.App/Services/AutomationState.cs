using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoRoute.App.Services;

/// <summary>
/// The one Auto-route on/off switch, shared by the top-bar toggle, the tray menu item and the
/// routing worker (App wires the worker to follow it). Off: AutoRoute leaves the graph alone —
/// saved routes aren't re-applied — until it's turned back on.
/// </summary>
public sealed partial class AutomationState : ObservableObject
{
    [ObservableProperty]
    private bool _enabled = true;
}
