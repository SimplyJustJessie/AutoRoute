using System.Collections.Generic;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// A persistent palette entry — one draggable Source (app-granularity). Dragging it into a column
/// connects it and leaves the entry in the palette, so one Source can populate many columns (fan-out).
/// The drag payload is <see cref="RepresentativeNodeId"/> (the Source node identity).
/// </summary>
public partial class SourceItemViewModel : ViewModelBase
{
    private readonly IBoardCoordinator _coordinator;

    /// <summary>Diff-merge key (Source identity).</summary>
    public string Key { get; }

    [ObservableProperty]
    private int _representativeNodeId;

    public IReadOnlyList<int> AllNodeIds { get; private set; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>Sample rate + bit depth ("48 kHz · 24-bit"); empty when the graph doesn't report one.</summary>
    [ObservableProperty]
    private string _format = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindLabel), nameof(IsAppKind), nameof(IsCaptureKind), nameof(IsMonitorKind))]
    private SourceKind _kind;

    [ObservableProperty]
    private bool _isMonitor;

    [ObservableProperty]
    private bool _isProtected;

    [ObservableProperty]
    private bool _isVisible = true;

    public SourceItemViewModel(IBoardCoordinator coordinator, PaletteItemModel model)
    {
        _coordinator = coordinator;
        Key = model.Key;
        AllNodeIds = model.AllNodeIds;
        Apply(model);
    }

    public void Apply(PaletteItemModel model)
    {
        RepresentativeNodeId = model.RepresentativeNodeId;
        AllNodeIds = model.AllNodeIds;
        Title = model.Title;
        Subtitle = model.Subtitle;
        Format = model.Format;
        Kind = model.Kind;
        IsMonitor = model.IsMonitor;
        IsProtected = model.Protected;
    }

    /// <summary>Kind flags for XAML style triggers (icon tile tinting).</summary>
    public bool IsAppKind => Kind == SourceKind.AppStream;
    public bool IsCaptureKind => Kind == SourceKind.Capture;
    public bool IsMonitorKind => Kind == SourceKind.Monitor;

    public string KindLabel => Kind switch
    {
        SourceKind.AppStream => "app",
        SourceKind.Capture => "capture",
        SourceKind.Monitor => "monitor",
        _ => "source",
    };

    [RelayCommand]
    private Task ToggleProtect() => _coordinator.ToggleProtectAsync(RepresentativeNodeId);
}
