using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// Which board tab (Audio/Video) is showing — the one piece of state both boards' toolbars need to
/// read and write. Shared as a single instance between <see cref="BoardViewModel.Tabs"/> and
/// <see cref="VideoBoardViewModel.Tabs"/> (same object, injected once), so each board's own toolbar
/// can bind to it directly off its own DataContext — no reaching across to a sibling view or the
/// Window's DataContext, which is null for a moment during window construction.
/// </summary>
public partial class TabSelectionState : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAudioSelected), nameof(IsVideoSelected))]
    private bool _videoSelected;

    public bool IsAudioSelected => !VideoSelected;
    public bool IsVideoSelected => VideoSelected;

    [RelayCommand]
    private void SelectAudio() => VideoSelected = false;

    [RelayCommand]
    private void SelectVideo() => VideoSelected = true;
}
