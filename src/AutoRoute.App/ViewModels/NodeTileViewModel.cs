using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AutoRoute.App.Services;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// The tile + type line every row, sidebar entry and chip draws: an app letter tile or a device
/// glyph, the input/output badge, and the group header it may open.
/// </summary>
public abstract partial class NodeTileViewModel : ViewModelBase
{
    private static readonly ConcurrentDictionary<string, IBrush> Brushes = new();

    [ObservableProperty]
    private string _typeLine = string.Empty;

    [ObservableProperty]
    private string _initials = "?";

    [ObservableProperty]
    private IBrush _tileBrush = BrushFor(NodePresentation.NeutralTile);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLetterTile))]
    private NodeGlyph _glyph = NodeGlyph.Wave;

    /// <summary>Mic badge (sound goes in) vs speaker badge (sound comes out).</summary>
    [ObservableProperty]
    private bool _isInput;

    [ObservableProperty]
    private int _group;

    [ObservableProperty]
    private string _groupLabel = string.Empty;

    /// <summary>Set by the owning list: true on the first visible entry of each group.</summary>
    [ObservableProperty]
    private bool _showGroupHeader;

    public bool IsLetterTile => Glyph == NodeGlyph.App;

    protected void ApplyLook(NodeLook? look)
    {
        look ??= NodeLook.Empty;
        TypeLine = look.TypeLine;
        Initials = look.Initials;
        TileBrush = BrushFor(look.TileColor);
        Glyph = look.Glyph;
        IsInput = look.IsInput;
        Group = look.Group;
        GroupLabel = look.GroupLabel;
        OnLookApplied(look);
    }

    protected virtual void OnLookApplied(NodeLook look) { }

    private static IBrush BrushFor(string hex) =>
        Brushes.GetOrAdd(hex, h => new ImmutableSolidColorBrush(Color.Parse(h)));
}

/// <summary>
/// A tile that also owns a volume: a sink row, or a source in the sidebar/tray. The fader writes
/// through the coordinator (coalesced, see <see cref="VolumeSender"/>); graph refreshes bring the
/// real value back, except right after a local change, so a refresh that raced a drag can't yank
/// the knob back to a stale value.
/// </summary>
public abstract partial class VolumeNodeViewModel : NodeTileViewModel
{
    private const long EchoGuardMs = 1500;

    private bool _applying;
    private long _lastLocalVolume = long.MinValue / 2;
    private long _lastLocalMute = long.MinValue / 2;

    protected abstract IBoardCoordinator Coordinator { get; }

    /// <summary>The node ids the fader drives (one sink, or every stream of an app).</summary>
    protected abstract IReadOnlyList<int> VolumeNodeIds { get; }

    /// <summary>The nodes behind this entry, for the level meter.</summary>
    public IReadOnlyList<int> NodeIds => VolumeNodeIds;

    /// <summary>
    /// Live signal level before this entry's own volume, in fader units (0..1, the same cube-root
    /// scale as <see cref="Volume"/>), so the fader can draw <c>Level × Volume</c> as what comes out.
    /// Set by <see cref="Services.LevelMeterFeed"/> while the window shows; 0 otherwise.
    /// </summary>
    [ObservableProperty]
    private double _level;

    /// <summary>A live meter is feeding <see cref="Level"/> right now.</summary>
    [ObservableProperty]
    private bool _isMetered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercent))]
    private double _volume;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _hasVolume;

    /// <summary>Audio is flowing right now (drives the playing indicator).</summary>
    [ObservableProperty]
    private bool _isRunning;

    public string VolumePercent => Math.Round(Volume * 100).ToString("0");

    protected override void OnLookApplied(NodeLook look)
    {
        _applying = true;
        try
        {
            HasVolume = look.Volume is not null;
            var now = Environment.TickCount64;
            if (look.Volume is { } v && now - _lastLocalVolume > EchoGuardMs)
                Volume = Math.Clamp(v, 0, 1);
            if (now - _lastLocalMute > EchoGuardMs)
                IsMuted = look.Muted;
            IsRunning = look.IsRunning;
        }
        finally { _applying = false; }
    }

    partial void OnVolumeChanged(double value)
    {
        if (_applying) return;
        _lastLocalVolume = Environment.TickCount64;
        Coordinator.SetVolume(VolumeNodeIds, value);
    }

    [RelayCommand]
    private void ToggleMute()
    {
        _lastLocalMute = Environment.TickCount64;
        IsMuted = !IsMuted;
        Coordinator.SetMute(VolumeNodeIds, IsMuted);
    }
}
