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
        ConnectCommand = connect;
    }

    public string Title { get; }
    public string TypeLine { get; }
    public string Initials { get; }
    public IBrush TileBrush { get; }
    public NodeGlyph Glyph { get; }
    public bool IsLetterTile { get; }
    public bool IsInput { get; }
    public IAsyncRelayCommand ConnectCommand { get; }
}
