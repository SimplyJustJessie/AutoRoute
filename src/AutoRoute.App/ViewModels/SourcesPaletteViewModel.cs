using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AutoRoute.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// The persistent Sources palette: every draggable Source (app-granularity). Diff-merged by Source
/// identity so entries update in place across graph updates. Cards stay here after being dragged
/// into a column, enabling fan-out.
/// </summary>
public partial class SourcesPaletteViewModel : ViewModelBase
{
    private readonly IBoardCoordinator _coordinator;

    public ObservableCollection<SourceItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isEmpty = true;

    public SourcesPaletteViewModel(IBoardCoordinator coordinator) => _coordinator = coordinator;

    /// <summary>In-place diff-merge from a fresh palette snapshot.</summary>
    public void Merge(IReadOnlyList<PaletteItemModel> models)
    {
        var byKey = Items.ToDictionary(i => i.Key);
        var wanted = new HashSet<string>(models.Count);

        foreach (var m in models)
        {
            wanted.Add(m.Key);
            if (byKey.TryGetValue(m.Key, out var existing))
                existing.Apply(m);
            else
                Items.Add(new SourceItemViewModel(_coordinator, m));
        }

        for (var i = Items.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Items[i].Key))
                Items.RemoveAt(i);

        IsEmpty = Items.Count == 0;
    }
}
