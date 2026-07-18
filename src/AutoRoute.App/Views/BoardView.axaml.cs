using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AutoRoute.App.Views;

public partial class BoardView : UserControl
{
    public BoardView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
