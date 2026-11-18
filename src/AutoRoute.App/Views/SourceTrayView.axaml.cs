using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AutoRoute.App.Views;

public partial class SourceTrayView : UserControl
{
    public SourceTrayView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
