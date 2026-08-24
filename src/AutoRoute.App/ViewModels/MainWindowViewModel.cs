namespace AutoRoute.App.ViewModels;

/// <summary>
/// The window root VM: the Audio and Video boards plus which tab is showing. Both boards share the
/// same underlying graph/rule-store singletons (see <see cref="VideoBoardViewModel"/>) and the same
/// <see cref="TabSelectionState"/> instance (each board's own toolbar hosts the tab pills and binds
/// to <c>Tabs</c> directly) — this VM just needs it too, to gate which board's Panel is visible.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    public BoardViewModel Audio { get; }
    public VideoBoardViewModel Video { get; }
    public TabSelectionState Tabs { get; }

    public MainWindowViewModel(BoardViewModel audio, VideoBoardViewModel video, TabSelectionState tabs)
    {
        Audio = audio;
        Video = video;
        Tabs = tabs;
    }
}
