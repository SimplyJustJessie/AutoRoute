using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// One Target Sink column: a header (the Target) over a scrollable list of Source cards feeding it,
/// and a drop target. A drop raises <see cref="ConnectCommand"/> with the dropped Source's live node
/// id; the column forwards it to the coordinator as "connect Source → this Target".
/// </summary>
public partial class SinkColumnViewModel : ViewModelBase
{
    private readonly IBoardCoordinator _coordinator;

    /// <summary>Diff-merge key (the Target's stable identity).</summary>
    public string Key { get; }

    public int TargetNodeId { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AcceptsDrop))]
    private bool _isProtected;

    /// <summary>Set by the filter pass; false hides the whole column.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>True when the column has no Source cards (drives the "drop a Source here" hint).</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    public ObservableCollection<SourceCardViewModel> Cards { get; } = new();

    /// <summary>A Protected column is locked — drops are refused.</summary>
    public bool AcceptsDrop => !IsProtected;

    public SinkColumnViewModel(IBoardCoordinator coordinator, ColumnModel model)
    {
        _coordinator = coordinator;
        Key = model.Key;
        TargetNodeId = model.TargetNodeId;
        Apply(model);
    }

    /// <summary>In-place update from a fresh model (diff-merge, keyed by source identity).</summary>
    public void Apply(ColumnModel model)
    {
        Title = model.Title;
        Subtitle = model.Subtitle;
        IsProtected = model.Protected;
        MergeCards(model.Cards);
    }

    private void MergeCards(IReadOnlyList<CardModel> models)
    {
        var byKey = Cards.ToDictionary(c => c.Key);
        var wanted = new HashSet<string>(models.Count);

        foreach (var m in models)
        {
            wanted.Add(m.Key);
            if (byKey.TryGetValue(m.Key, out var existing))
                existing.Apply(m);
            else
                Cards.Add(new SourceCardViewModel(_coordinator, TargetNodeId, m));
        }

        for (var i = Cards.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Cards[i].Key))
                Cards.RemoveAt(i);

        IsEmpty = Cards.Count == 0;
    }

    /// <summary>Invoked by the drop behavior with the dropped Source's live node id.</summary>
    [RelayCommand]
    private Task Connect(int sourceNodeId) => _coordinator.ConnectAsync(sourceNodeId, TargetNodeId);

    /// <summary>Toggle "do not touch" (Protected) for this Target Sink node.</summary>
    [RelayCommand]
    private Task ToggleProtect() => _coordinator.ToggleProtectAsync(TargetNodeId);
}
