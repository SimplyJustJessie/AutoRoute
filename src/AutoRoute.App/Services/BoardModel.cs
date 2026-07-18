using System.Collections.Generic;
using AutoRoute.App.ViewModels;

namespace AutoRoute.App.Services;

/// <summary>Immutable description of one Source card, produced by <see cref="BoardModelBuilder"/>.</summary>
/// <param name="Key">Stable per-column card key (source identity) used for diff-merge.</param>
/// <param name="RepresentativeNodeId">One live node id backing this card (for port lookup on actions).</param>
/// <param name="SourceIdentity">App-granularity identity of the Source.</param>
/// <param name="AllSourceNodeIds">Every live node id this app-granularity card represents.</param>
public sealed record CardModel(
    string Key,
    int RepresentativeNodeId,
    string SourceIdentity,
    IReadOnlyList<int> AllSourceNodeIds,
    string Title,
    string Subtitle,
    CardState State,
    string? RuleId,
    string? RuleName,
    int? LinkId,
    string Tooltip);

/// <summary>Immutable description of one Target Sink column.</summary>
public sealed record ColumnModel(
    int TargetNodeId,
    string Key,
    string Title,
    string Subtitle,
    bool Protected,
    IReadOnlyList<CardModel> Cards);

/// <summary>Immutable description of one palette Source entry (app-granularity).</summary>
public sealed record PaletteItemModel(
    string Key,
    int RepresentativeNodeId,
    IReadOnlyList<int> AllNodeIds,
    string Title,
    string Subtitle,
    SourceKind Kind,
    bool IsMonitor,
    bool Protected);

/// <summary>The whole board in one immutable snapshot: columns + palette.</summary>
public sealed record BoardSnapshot(
    IReadOnlyList<ColumnModel> Columns,
    IReadOnlyList<PaletteItemModel> Palette);
