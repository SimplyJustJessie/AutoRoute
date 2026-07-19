using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AutoRoute.App.Views;

public partial class SinkColumnView : UserControl
{
    public SinkColumnView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Close the confirm flyout on Delete — but DEFERRED. Button.OnClick raises Click before it
    // invokes the bound Command; hiding the flyout synchronously here tears down the popup's
    // DataContext, which nulls the Command binding and silently skips the invoke (the "Delete sink
    // does nothing" bug). Posting the hide runs it strictly after the command has been dispatched.
    private void OnConfirmDeleteSinkClick(object? sender, RoutedEventArgs e)
    {
        var flyout = this.FindControl<Button>("DeleteSinkButton")?.Flyout;
        Dispatcher.UIThread.Post(() => flyout?.Hide());
    }
}
