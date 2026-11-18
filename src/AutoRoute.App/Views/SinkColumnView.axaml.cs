using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AutoRoute.App.Views;

public partial class SinkColumnView : UserControl
{
    public SinkColumnView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Close a flyout after one of its buttons fires — but DEFERRED. Button.OnClick raises Click
    // before it invokes the bound Command; hiding the flyout synchronously tears down the popup's
    // DataContext, which nulls the Command binding and silently skips the invoke. Posting the hide
    // runs it strictly after the command has been dispatched.
    private void OnConfirmDeleteSinkClick(object? sender, RoutedEventArgs e) => HideFlyoutOf("RowMenuButton");

    private void OnPickClick(object? sender, RoutedEventArgs e) => HideFlyoutOf("AddButton");

    private void HideFlyoutOf(string buttonName)
    {
        var flyout = this.FindControl<Button>(buttonName)?.Flyout;
        Dispatcher.UIThread.Post(() => flyout?.Hide());
    }
}
