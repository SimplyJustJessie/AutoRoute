using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AutoRoute.App.Views;

public partial class MainWindow : Window
{
    // Frame CornerRadius (10) minus its 1 px border: the content's clip sits just inside the line.
    private const double ContentRadius = 9;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        if (this.FindControl<Grid>("FrameContent") is { } content)
        {
            content.SizeChanged += (_, _) => UpdateContentClip(content);
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty) UpdateContentClip(content);
            };
        }
    }

    // Rounded clip for the window content, so nothing paints past the frame's rounded corners.
    // Maximized windows are square (BoardStyles), so they get no clip at all.
    private void UpdateContentClip(Control content)
    {
        content.Clip = WindowState == WindowState.Maximized
            ? null
            : new RectangleGeometry(new Rect(content.Bounds.Size), ContentRadius, ContentRadius);
    }
}
