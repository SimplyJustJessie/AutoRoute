using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AutoRoute.App.Views;

public partial class SinkColumnView : UserControl
{
    public SinkColumnView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
