using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AutoRoute.App.Views;

public partial class BoardView : UserControl
{
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
}
