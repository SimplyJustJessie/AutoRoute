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
/// Audio vs. Video is decided by <c>media.class</c> (see <see cref="MediaKind"/>) so e.g. video
/// streams (kwin/plasmashell, a Spout2PW sender) never clutter the Audio board and vice versa.
/// App-granularity (ADR-0003): the identity key collapses an app's many ephemeral streams into one.
/// </summary>
public static class NodeRoles
{
    private static readonly StringComparison Ord = StringComparison.OrdinalIgnoreCase;

    /// <summary>An audio node (its <c>media.class</c> mentions Audio).</summary>
    public static bool IsAudio(PwNode n) => n.MediaClass is { } mc && mc.Contains("Audio", Ord);

    /// <summary>A video node (its <c>media.class</c> mentions Video) — e.g. a Spout2PW sender, OBS's PipeWire video capture.</summary>
    public static bool IsVideo(PwNode n) => n.MediaClass is { } mc && mc.Contains("Video", Ord);

    /// <summary>Whether a node belongs to the given board's media kind.</summary>
    public static bool IsOfKind(PwNode n, MediaKind kind) => kind == MediaKind.Video ? IsVideo(n) : IsAudio(n);

    /// <summary>A draggable Source: audio + has output ports (app stream, capture, or a sink's monitor).</summary>
    public static bool IsAudioSource(PwNode n) => IsSource(n, MediaKind.Audio);

    /// <summary>A draggable Source of the given kind: has output ports (app stream, capture, or a sink's monitor).</summary>
    public static bool IsSource(PwNode n, MediaKind kind) => IsOfKind(n, kind) && n.HasOutputPorts;

    /// <summary>A Target Sink column candidate: audio + has input ports (hardware/null sink or app-input target).</summary>
    public static bool IsTargetSink(PwNode n) => IsTargetSink(n, MediaKind.Audio);

    /// <summary>A Target Sink column candidate of the given kind: has input ports.</summary>
    public static bool IsTargetSink(PwNode n, MediaKind kind) => IsOfKind(n, kind) && n.HasInputPorts;

    /// <summary>A sink appearing as a Source only through its monitor outputs (the "off by default" group).</summary>
    public static bool IsMonitorSink(PwNode n) => IsMonitorSink(n, MediaKind.Audio);

    /// <summary>A sink of the given kind appearing as a Source only through its monitor outputs.</summary>
    public static bool IsMonitorSink(PwNode n, MediaKind kind) =>
        n.MediaClass is { } mc && mc.Equals(SinkClass(kind), Ord) && n.HasOutputPorts;

    public static SourceKind KindOf(PwNode n) => KindOf(n, MediaKind.Audio);

    public static SourceKind KindOf(PwNode n, MediaKind kind)
    {
        var mc = n.MediaClass ?? string.Empty;
        if (mc.Equals(SinkClass(kind), Ord)) return SourceKind.Monitor;
        if (mc.Contains("Source", Ord)) return SourceKind.Capture;
        if (mc.Contains("Stream/Output", Ord)) return SourceKind.AppStream;
        return SourceKind.Other;
    }

    private static string SinkClass(MediaKind kind) => kind == MediaKind.Video ? "Video/Sink" : "Audio/Sink";

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

    /// <summary>The node's sample rate + bit depth as one line ("48 kHz · 24-bit"), or empty when unknown.</summary>
    public static string FormatLabel(PwNode n) => n.Format?.Summary ?? string.Empty;

    /// <summary>First non-empty candidate that isn't just the title again (no echo subtitles).</summary>
    private static string FirstOtherThan(string title, params string?[] candidates)
    {
        foreach (var c in candidates)
            if (Norm(c) is { } v && !v.Equals(title, Ord)) return v;
        return string.Empty;
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
