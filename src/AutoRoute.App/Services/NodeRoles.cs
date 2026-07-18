using System;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Services;

/// <summary>How a Source presents in the palette (drives grouping + the "monitors off by default" toggle).</summary>
public enum SourceKind
{
    /// <summary>An app playback stream (<c>Stream/Output/Audio</c>) — Zen, Spotify, …</summary>
    AppStream,

    /// <summary>A capture device or virtual source (<c>Audio/Source</c> / <c>Audio/Source/Virtual</c>).</summary>
    Capture,

    /// <summary>A sink surfaced as a Source via its monitor ports (off by default).</summary>
    Monitor,

    /// <summary>Anything else with audio output ports.</summary>
    Other,
}

/// <summary>
/// Pure classification of <see cref="PwNode"/>s into the board's vocabulary
/// (Source / Target Sink / monitor) plus the stable identity + display strings the UI keys on.
/// "Audio" is decided by <c>media.class</c> so video streams (kwin/plasmashell) never clutter the board.
/// App-granularity (ADR-0003): the identity key collapses an app's many ephemeral streams into one.
/// </summary>
public static class NodeRoles
{
    private static readonly StringComparison Ord = StringComparison.OrdinalIgnoreCase;

    /// <summary>An audio node (its <c>media.class</c> mentions Audio).</summary>
    public static bool IsAudio(PwNode n) => n.MediaClass is { } mc && mc.Contains("Audio", Ord);

    /// <summary>A draggable Source: audio + has output ports (app stream, capture, or a sink's monitor).</summary>
    public static bool IsAudioSource(PwNode n) => IsAudio(n) && n.HasOutputPorts;

    /// <summary>A Target Sink column candidate: audio + has input ports (hardware/null sink or app-input target).</summary>
    public static bool IsTargetSink(PwNode n) => IsAudio(n) && n.HasInputPorts;

    /// <summary>A sink appearing as a Source only through its monitor outputs (the "off by default" group).</summary>
    public static bool IsMonitorSink(PwNode n) =>
        n.MediaClass is { } mc && mc.Equals("Audio/Sink", Ord) && n.HasOutputPorts;

    public static SourceKind KindOf(PwNode n)
    {
        var mc = n.MediaClass ?? string.Empty;
        if (mc.Equals("Audio/Sink", Ord)) return SourceKind.Monitor;
        if (mc.Contains("Source", Ord)) return SourceKind.Capture;
        if (mc.Contains("Stream/Output", Ord)) return SourceKind.AppStream;
        return SourceKind.Other;
    }

    /// <summary>Stable identity of a Source, collapsing an app's streams to one (app granularity).</summary>
    public static string SourceIdentity(PwNode n) =>
        Norm(n.ApplicationName) ?? Norm(n.NodeName) ?? Norm(n.MediaName) ?? ("id:" + n.Id);

    /// <summary>Stable identity of a Target Sink.</summary>
    public static string TargetIdentity(PwNode n) =>
        Norm(n.NodeName) ?? Norm(n.Description) ?? Norm(n.ApplicationName) ?? ("id:" + n.Id);

    // Display strings lead with the human-readable name (app name / device description) and fall
    // back to the raw node name — which then moves to the subtitle so it stays discoverable.
    // Identity (matching, diff-merge keys) is untouched: these are presentation-only.

    public static string SourceTitle(PwNode n) =>
        Norm(n.ApplicationName) ?? Norm(n.Description) ?? Norm(n.NodeName) ?? Norm(n.MediaName) ?? ("Node " + n.Id);

    public static string SourceSubtitle(PwNode n) =>
        FirstOtherThan(SourceTitle(n), n.MediaName, n.NodeName, n.Description, n.MediaClass, n.ProcessBinary);

    public static string TargetTitle(PwNode n) =>
        Norm(n.Description) ?? Norm(n.ApplicationName) ?? Norm(n.NodeName) ?? ("Node " + n.Id);

    public static string TargetSubtitle(PwNode n) =>
        FirstOtherThan(TargetTitle(n), n.NodeName, n.ApplicationName, n.MediaClass);

    /// <summary>First non-empty candidate that isn't just the title again (no echo subtitles).</summary>
    private static string FirstOtherThan(string title, params string?[] candidates)
    {
        foreach (var c in candidates)
            if (Norm(c) is { } v && !v.Equals(title, Ord)) return v;
        return string.Empty;
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
