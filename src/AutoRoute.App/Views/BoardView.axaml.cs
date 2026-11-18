using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AutoRoute.App.Views;

public partial class BoardView : UserControl
{
    private Window? _trackedWindow;

    public BoardView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Close the create-sink flyout on Create — but DEFERRED. Button.OnClick raises Click before it
    // invokes the bound Command; hiding the flyout synchronously here tears down the popup's
    // DataContext, which nulls the Command binding and silently skips the invoke (the "Create does
    // nothing" bug). Posting the hide runs it strictly after the command has been dispatched.
    private void OnCreateSinkClick(object? sender, RoutedEventArgs e)
    {
        var flyout = this.FindControl<Button>("NewSinkButton")?.Flyout;
        Dispatcher.UIThread.Post(() => flyout?.Hide());
    }

    // === Client-side window chrome (single-row title bar) ==============================
    // The toolbar is the window's title bar, so its caption buttons and drag/maximise gestures
    // reach the hosting Window through the visual tree. Guarded everywhere for the headless
    // screenshot host and design-time, where the root may not be a Window.

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.GetVisualRoot() is Window window)
        {
            _trackedWindow = window;
            window.PropertyChanged += OnWindowPropertyChanged;
            UpdateMaximizeGlyph(window);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_trackedWindow is { } w)
            w.PropertyChanged -= OnWindowPropertyChanged;
        _trackedWindow = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && sender is Window window)
            UpdateMaximizeGlyph(window);
    }

    private Window? HostWindow => this.GetVisualRoot() as Window;

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A child control (button, textbox, toggle) marks the event handled first — only bare
        // chrome starts a window move, and only with the primary button.
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        HostWindow?.BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInteractiveSource(e.Source))
            return;
        ToggleMaximize();
    }

    // A window-edge/corner grip: start a native resize drag in the grip's direction. The WindowEdge
    // is carried on each grip's Tag (set in XAML). No-op off a Window (headless/design) or when the
    // window is maximised (nothing to resize).
    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (HostWindow is not { } w || w.WindowState == WindowState.Maximized) return;
        if (sender is Border { Tag: string edgeName } && Enum.TryParse<WindowEdge>(edgeName, out var edge))
            w.BeginResizeDrag(edge, e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } w) w.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => HostWindow?.Close();

    private void ToggleMaximize()
    {
        if (HostWindow is not { } w) return;
        w.WindowState = w.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeGlyph(Window window)
    {
        var icon = this.FindControl<Path>("MaximizeIcon");
        if (icon is null) return;
        var key = window.WindowState == WindowState.Maximized ? "IconWindowRestore" : "IconWindowMax";
        if (this.TryFindResource(key, out var geometry) && geometry is Avalonia.Media.Geometry g)
            icon.Data = g;
    }

    // Walk up from the tapped element; a Button/TextBox/etc. ancestor means the gesture belongs to
    // that control, not the title bar.
    private static bool IsInteractiveSource(object? source)
    {
        for (var v = source as Visual; v is not null && v is not Window; v = v.GetVisualParent())
        {
            if (v is Button or TextBox or ToggleSwitch or RadioButton)
                return true;
        }
        return false;
    }
}
