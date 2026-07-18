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

    /// <summary>The node's <c>node.name</c> — the identity sink management keys on (null → not manageable).</summary>
    public string? SinkName { get; private set; }

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
        SinkName = model.SinkName;
        IsManagedSink = model.IsManagedSink;
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

    /// <summary>True when this column is an AutoRoute-declared virtual sink — shows the VIRTUAL chip + delete affordance.</summary>
    [ObservableProperty]
    private bool _isManagedSink;

    /// <summary>Human summary of what the delete confirm flyout is about to take along.</summary>
    [ObservableProperty]
    private string _deleteImpactText = string.Empty;

    /// <summary>True when any Rule/Suppression references this sink (drives the flyout checkbox row).</summary>
    [ObservableProperty]
    private bool _hasDeleteImpact;

    /// <summary>"Also delete these" — default checked, per the delete-sink decision.</summary>
    [ObservableProperty]
    private bool _deleteAffectedPolicy = true;

    /// <summary>Invoked by the drop behavior with the dropped Source's live node id.</summary>
    [RelayCommand]
    private Task Connect(int sourceNodeId) => _coordinator.ConnectAsync(sourceNodeId, TargetNodeId);

    /// <summary>Toggle "do not touch" (Protected) for this Target Sink node.</summary>
    [RelayCommand]
    private Task ToggleProtect() => _coordinator.ToggleProtectAsync(TargetNodeId);

    /// <summary>Refreshes the impact preview — runs when the delete button opens its confirm flyout.</summary>
    [RelayCommand]
    private void PrepareDeleteSink()
    {
        if (SinkName is null) return;
        var impact = _coordinator.PreviewDeleteSink(SinkName);
        HasDeleteImpact = !impact.IsEmpty;
        DeleteAffectedPolicy = true;

        if (impact.IsEmpty)
        {
            DeleteImpactText = "No rules reference this sink.";
            return;
        }

        var parts = new List<string>();
        foreach (var rule in impact.Rules) parts.Add($"• Rule: {rule.Name}");
        if (impact.Suppressions.Count > 0)
            parts.Add($"• {impact.Suppressions.Count} suppression(s)");
        DeleteImpactText = string.Join("\n", parts);
    }

    /// <summary>Confirmed delete: sink (+ affected policy when checked) in one save, then unload.</summary>
    [RelayCommand]
    private Task ConfirmDeleteSink() =>
        SinkName is null ? Task.CompletedTask : _coordinator.DeleteSinkAsync(SinkName, DeleteAffectedPolicy);
}
