using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AutoRoute.App.Views;

public partial class SourceCardView : UserControl
{
    public SourceCardView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
