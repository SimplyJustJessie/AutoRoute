using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AutoRoute.App.Views;

public partial class BoardView : UserControl
{
    public BoardView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Close the create-sink flyout on Create; the work itself runs via SubmitNewSinkCommand.
    // A disabled command (invalid form) never raises Click, so this only fires on real submits.
    private void OnCreateSinkClick(object? sender, RoutedEventArgs e) =>
        this.FindControl<Button>("NewSinkButton")?.Flyout?.Hide();
}
