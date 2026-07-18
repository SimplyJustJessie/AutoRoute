using System.Collections.Generic;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// One Source card inside a Target Sink column: a Managed / Manual / Unsaved / Protected Link,
/// with the per-card actions (remove, save, keep-manual, protect). State-derived booleans drive
/// XAML style triggers; the actual work is delegated to the <see cref="IBoardCoordinator"/>.
/// </summary>
public partial class SourceCardViewModel : ViewModelBase
{
    private readonly IBoardCoordinator _coordinator;

    /// <summary>Diff-merge key within a column (the Source's app-granularity identity).</summary>
    public string Key { get; }

    public int TargetNodeId { get; }

    [ObservableProperty]
    private int _representativeNodeId;

    /// <summary>Every live node id this app-granularity card stands for (all of an app's streams).</summary>
    public IReadOnlyList<int> AllSourceNodeIds { get; private set; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _tooltip = string.Empty;

    [ObservableProperty]
    private string? _ruleId;

    [ObservableProperty]
    private int? _linkId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManaged), nameof(IsManual), nameof(IsUnsaved), nameof(IsProtected))]
    [NotifyPropertyChangedFor(nameof(CanRemove), nameof(CanSave), nameof(CanKeepManual))]
    private CardState _state;

    /// <summary>Set by the filter pass; false hides the card without dropping it from the collection.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    public SourceCardViewModel(IBoardCoordinator coordinator, CardModel model)
    {
        _coordinator = coordinator;
        Key = model.Key;
        TargetNodeId = -1; // set by column
        AllSourceNodeIds = model.AllSourceNodeIds;
        Apply(model);
    }

    public SourceCardViewModel(IBoardCoordinator coordinator, int targetNodeId, CardModel model)
    {
        _coordinator = coordinator;
        Key = model.Key;
        TargetNodeId = targetNodeId;
        AllSourceNodeIds = model.AllSourceNodeIds;
        Apply(model);
    }

    /// <summary>In-place update from a fresh model (diff-merge: no card recreation → no flicker).</summary>
    public void Apply(CardModel model)
    {
        RepresentativeNodeId = model.RepresentativeNodeId;
        AllSourceNodeIds = model.AllSourceNodeIds;
        Title = model.Title;
        Subtitle = model.Subtitle;
        Tooltip = model.Tooltip;
        RuleId = model.RuleId;
        LinkId = model.LinkId;
        State = model.State;
    }

    public bool IsManaged => State == CardState.Managed;
    public bool IsManual => State == CardState.Manual;
    public bool IsUnsaved => State == CardState.Unsaved;
    public bool IsProtected => State == CardState.Protected;

    public bool CanRemove => State != CardState.Protected;
    public bool CanSave => State == CardState.Unsaved;
    public bool CanKeepManual => State == CardState.Unsaved;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task Remove() => _coordinator.RemoveCardAsync(this);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task Save() => _coordinator.SaveCardAsync(this);

    [RelayCommand(CanExecute = nameof(CanKeepManual))]
    private void KeepManual() => _coordinator.KeepManual(this);

    [RelayCommand]
    private Task ToggleProtect() => _coordinator.ToggleProtectAsync(RepresentativeNodeId);
}
