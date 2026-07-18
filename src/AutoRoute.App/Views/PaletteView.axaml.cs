using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AutoRoute.App.Views;

public partial class PaletteView : UserControl
{
    public PaletteView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
