using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AutoRoute.App.Views;

public partial class SinkColumnView : UserControl
{
    public SinkColumnView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Flyouts only light-dismiss; confirming the delete should close it too. (UI-only — the
    // delete itself runs via the button's bound ConfirmDeleteSinkCommand.)
    private void OnConfirmDeleteSinkClick(object? sender, RoutedEventArgs e) =>
        this.FindControl<Button>("DeleteSinkButton")?.Flyout?.Hide();
}
