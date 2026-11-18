using Avalonia.Media;
using AutoRoute.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace AutoRoute.App.ViewModels;

/// <summary>One entry in an output row's "+ Add" picker: a source and the command that routes it here.</summary>
public sealed class AddOption
{
    public AddOption(SourceItemViewModel source, IAsyncRelayCommand connect)
    {
        Title = source.Title;
        TypeLine = source.TypeLine;
        Initials = source.Initials;
        TileBrush = source.TileBrush;
        Glyph = source.Glyph;
        IsLetterTile = source.IsLetterTile;
        IsInput = source.IsInput;
        AppIcon = source.AppIcon;
        HasAppIcon = source.HasAppIcon;
        ShowLetter = source.ShowLetter;
        TileFill = source.TileFill;
        ConnectCommand = connect;
    }

    public string Title { get; }
    public string TypeLine { get; }
    public string Initials { get; }
    public IBrush TileBrush { get; }
    public NodeGlyph Glyph { get; }
    public bool IsLetterTile { get; }
    public bool IsInput { get; }
    public Avalonia.Media.Imaging.Bitmap? AppIcon { get; }
    public bool HasAppIcon { get; }
    public bool ShowLetter { get; }
    public IBrush TileFill { get; }
    public IAsyncRelayCommand ConnectCommand { get; }
}
