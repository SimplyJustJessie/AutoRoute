using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AutoRoute.App.Hosting;

/// <summary>
/// The native tray icon and its menu (PLAN "Process, tray, autostart"). Uses Avalonia's
/// <see cref="TrayIcon"/> + <see cref="NativeMenu"/> (StatusNotifierItem/DBus on Plasma/Wayland).
/// Menu items: <b>Open</b> (reveal the window), <b>Automation Enabled</b> (checkbox bound to the
/// <see cref="RoutingWorker"/> flag), and <b>Quit</b> (the only real shutdown).
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _automationItem;
    private readonly Application _app;

    public TrayController(Application app, RoutingWorker worker, Action onOpen, Action onQuit)
    {
        _app = app;

        var openItem = new NativeMenuItem("Open");
        openItem.Click += (_, _) => onOpen();

        _automationItem = new NativeMenuItem("Automation Enabled")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = worker.AutomationEnabled,
        };
        _automationItem.Click += (_, _) =>
        {
            var next = !worker.AutomationEnabled;
            worker.AutomationEnabled = next;
            _automationItem.IsChecked = next;
        };

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => onQuit();

        var menu = new NativeMenu
        {
            Items =
            {
                openItem,
                _automationItem,
                new NativeMenuItemSeparator(),
                quitItem,
            },
        };

        _trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "AutoRoute",
            Menu = menu,
            IsVisible = true,
        };
        // Left-click / activate on the icon reveals the window (in addition to the Open menu item).
        _trayIcon.Clicked += (_, _) => onOpen();

        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://AutoRoute.App/Assets/tray-icon.png"));
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        // Detach from the application so a fresh instance can re-register cleanly.
        TrayIcon.SetIcons(_app, new TrayIcons());
    }
}
