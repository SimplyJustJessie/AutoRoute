using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Services;

/// <summary>What a node's tile shows when there is no app letter tile.</summary>
public enum NodeGlyph { App, Headset, Mic, Speaker, Monitor, Interface, Virtual, Wave, Camera }

/// <summary>Where an output row sits on the board.</summary>
public enum TargetGroup { Virtual = 0, Device = 1, AppInput = 2 }

/// <summary>Where a source sits in the sidebar / tray.</summary>
public enum SourceGroup { App = 0, Input = 1, Monitor = 2 }

/// <summary>
/// Everything a card, chip or row needs to look right, computed once per snapshot by
/// <see cref="BoardModelBuilder"/>. Presentation only: identity and matching never read it.
/// </summary>
/// <param name="TypeLine">One or two words for what the thing is: "USB", "GPU", "Onboard", "Virtual", "Mic input", or an app stream's media name. Empty when the group header already says it.</param>
/// <param name="Glyph">Device/kind glyph; <see cref="NodeGlyph.App"/> means "draw the letter tile".</param>
/// <param name="IsInput">True draws the mic badge (sound goes in here), false the speaker badge.</param>
/// <param name="Initials">One or two letters for the app tile.</param>
/// <param name="TileColor">Hex background for the app tile, stable per identity.</param>
/// <param name="Group">Sort/group index (a <see cref="TargetGroup"/> or <see cref="SourceGroup"/>).</param>
/// <param name="GroupLabel">Group header text ("Virtual", "Devices", "App inputs", "Apps", "Inputs", "Monitors").</param>
/// <param name="Volume">Cubic-scale volume (1.0 = 100%), null when the node exposes none.</param>
/// <param name="Muted">Muted right now.</param>
/// <param name="IsRunning">Audio is flowing (PipeWire node state <c>running</c>).</param>
public sealed record NodeLook(
    string TypeLine,
    NodeGlyph Glyph,
    bool IsInput,
    string Initials,
    string TileColor,
    int Group,
    string GroupLabel,
    double? Volume,
    bool Muted,
    bool IsRunning)
{
    public static readonly NodeLook Empty =
        new(string.Empty, NodeGlyph.Wave, false, "?", NodePresentation.NeutralTile, 0, string.Empty, null, false, false);
}

public static class NodePresentation
{
    public const string NeutralTile = "#22242B";

    // Muted, mid-lightness tiles that keep white initials readable (≥ 4.5:1) on every entry.
    private static readonly string[] TilePalette =
    {
        "#4B5BD6", "#6B3FA0", "#2E7D6B", "#B4542D", "#8C3B5E", "#3D6E9E", "#7A6224", "#5A6B2E",
    };

    // app-stream media names that say nothing a human cares about
    private static readonly HashSet<string> GenericMediaNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Playback", "RecordStream", "Record", "audio stream", "Audio Stream", "output", "input",
        "Simple DirectMedia Layer", "ALSA Playback", "ALSA Capture", "Chromium", "WEBRTC VoiceEngine",
    };

    // ALSA profile suffixes on node.description ("… Analog Stereo", "… Mono", "… Digital Stereo (HDMI)").
    private static readonly Regex ProfileSuffix = new(
        @"\s+(?:(?:Analog|Digital|Pro|Multichannel|Headset|Handsfree)\s+)?(?:Stereo|Mono|Surround(?:\s+[\d.]+)?|Input|Output)(?:\s*\([^)]*\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HdmiNick = new(@"^(HDMI|DisplayPort|DP)\s*\d*$", RegexOptions.IgnoreCase);

    /// <summary>True for nodes backed by hardware (they carry a <c>device.bus</c>).</summary>
    public static bool IsDevice(PwNode n) => n.Info.DeviceBus is { Length: > 0 };

    public static bool IsHdmi(PwNode n) =>
        Contains(n.Description, "HDMI") || Contains(n.Description, "DisplayPort")
        || (n.Info.Nick is { } nick && HdmiNick.IsMatch(nick.Trim()));

    /// <summary>
    /// A device's human name: "PRO X 2 LIGHTSPEED Analog Stereo" → "PRO X 2 LIGHTSPEED",
    /// HDMI outputs → their nick ("HDMI 0"). Null for non-device nodes (callers keep their own title).
    /// </summary>
    public static string? DeviceName(PwNode n)
    {
        if (!IsDevice(n)) return null;
        if (IsHdmi(n) && n.Info.Nick is { } nick && HdmiNick.IsMatch(nick.Trim()))
            return nick.Trim();
        var desc = n.Description?.Trim();
        if (string.IsNullOrEmpty(desc)) return null;
        var stripped = ProfileSuffix.Replace(desc, string.Empty).Trim();
        return stripped.Length > 0 ? stripped : desc;
    }

    public static NodeLook ForTarget(PwNode n, PwGraph graph, string title, MediaKind kind)
    {
        var group = TargetGroupOf(n);
        var typeLine = group switch
        {
            TargetGroup.AppInput => "Mic input",
            TargetGroup.Virtual => string.Empty, // the "Virtual" header already says it
            _ => BusLine(n),
        };
        var glyph = kind == MediaKind.Video ? NodeGlyph.Camera
            : group == TargetGroup.AppInput ? NodeGlyph.App
            : TargetGlyph(n, graph);
        // A virtual source you can route into (Easy Effects Source) is still a mic.
        var isInput = group == TargetGroup.AppInput
            || (n.MediaClass?.Contains("Source", StringComparison.OrdinalIgnoreCase) ?? false);
        return Build(n, title, typeLine, glyph, isInput,
            (int)group, group switch
            {
                TargetGroup.Virtual => "Virtual",
                TargetGroup.Device => "Devices",
                _ => "App inputs",
            });
    }

    public static NodeLook ForSource(PwNode n, PwGraph graph, string title, SourceKind sourceKind, MediaKind kind)
    {
        var group = sourceKind switch
        {
            SourceKind.Monitor => SourceGroup.Monitor,
            SourceKind.Capture => SourceGroup.Input,
            _ => SourceGroup.App,
        };
        string typeLine;
        NodeGlyph glyph;
        switch (group)
        {
            case SourceGroup.Monitor:
                typeLine = "Monitor";
                glyph = TargetGlyph(n, graph);
                break;
            case SourceGroup.Input:
                typeLine = IsDevice(n) ? BusLine(n) : "Virtual";
                glyph = IsDevice(n) ? SourceDeviceGlyph(n, graph) : AppishGlyph(n);
                break;
            default:
                typeLine = MediaLine(n, title);
                glyph = NodeGlyph.App;
                break;
        }
        if (kind == MediaKind.Video) glyph = NodeGlyph.Camera;
        return Build(n, title, typeLine, glyph, isInput: group == SourceGroup.Input, (int)group,
            group switch
            {
                SourceGroup.App => "Apps",
                SourceGroup.Input => "Inputs",
                _ => "Monitors",
            });
    }

    /// <summary>Letter tile for a chip: initials + stable color from the source identity.</summary>
    public static (string Initials, string Color) Tile(string title, string identity) =>
        (Initials(title), ColorFor(identity));

    public static string Initials(string title)
    {
        var words = title.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("Source", StringComparison.OrdinalIgnoreCase)
                     && !w.Equals("Sink", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (words.Count == 0) return title.Length > 0 ? title[..1].ToUpperInvariant() : "?";
        if (words.Count == 1) return words[0][..1].ToUpperInvariant();
        return string.Concat(words[0][..1], words[1][..1]).ToUpperInvariant();
    }

    public static string ColorFor(string identity)
    {
        // FNV-1a: stable across runs (string.GetHashCode is randomized per process).
        uint h = 2166136261;
        foreach (var c in identity.ToLowerInvariant()) { h ^= c; h *= 16777619; }
        return TilePalette[h % (uint)TilePalette.Length];
    }

    private static NodeLook Build(PwNode n, string title, string typeLine, NodeGlyph glyph, bool isInput,
        int group, string groupLabel)
    {
        var identity = n.ApplicationName ?? n.Info.ApplicationId ?? n.NodeName ?? title;
        return new NodeLook(
            TypeLine: typeLine,
            Glyph: glyph,
            IsInput: isInput,
            Initials: Initials(title),
            TileColor: glyph == NodeGlyph.App ? ColorFor(identity) : NeutralTile,
            Group: group,
            GroupLabel: groupLabel,
            Volume: n.Info.Volume,
            Muted: n.Info.Muted ?? false,
            IsRunning: n.Info.IsRunning);
    }

    private static TargetGroup TargetGroupOf(PwNode n)
    {
        if (n.IsCaptureStream) return TargetGroup.AppInput;
        if (IsDevice(n)) return TargetGroup.Device;
        return TargetGroup.Virtual;
    }

    private static string BusLine(PwNode n) => n.Info.DeviceBus?.ToLowerInvariant() switch
    {
        "usb" => "USB",
        "bluetooth" => "Bluetooth",
        "pci" => IsHdmi(n) ? "GPU" : "Onboard",
        null or "" => string.Empty,
        var other => other.ToUpperInvariant(),
    };

    private static string MediaLine(PwNode n, string title)
    {
        var media = n.MediaName?.Trim();
        if (string.IsNullOrEmpty(media) || GenericMediaNames.Contains(media)
            || media.Equals(title, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return media;
    }

    private static NodeGlyph TargetGlyph(PwNode n, PwGraph graph)
    {
        if (!IsDevice(n)) return AppishGlyph(n) == NodeGlyph.App ? NodeGlyph.App : NodeGlyph.Virtual;
        if (IsHdmi(n)) return NodeGlyph.Monitor;
        if (IsHeadset(n, graph)) return NodeGlyph.Headset;
        return n.Info.DeviceBus == "usb" ? NodeGlyph.Interface : NodeGlyph.Speaker;
    }

    // A headset's boom mic draws as a mic, never the headset: as a chip on some output it would
    // otherwise look exactly like the headphones themselves.
    private static NodeGlyph SourceDeviceGlyph(PwNode n, PwGraph graph) =>
        IsHeadset(n, graph) ? NodeGlyph.Mic : NodeGlyph.Interface;

    /// <summary>
    /// A source's display name. A capture device that also has an output on the same hardware gets
    /// " mic" (headset) or " input" (interface) appended, since ALSA names both sides the same and
    /// "PRO X 2 LIGHTSPEED" on a chip would read as the headphones.
    /// </summary>
    public static string SourceName(PwNode n, PwGraph graph)
    {
        var title = NodeRoles.SourceTitle(n);
        if (!IsDevice(n) || n.MediaClass is not "Audio/Source" || n.Info.DeviceId is not { } dev) return title;
        var hasOutput = graph.Nodes.Any(o => o.Info.DeviceId == dev && o.MediaClass is "Audio/Sink");
        if (!hasOutput) return title;
        return title + (IsHeadset(n, graph) ? " mic" : " input");
    }

    // Virtual nodes an app owns (Easy Effects' sink/source) get that app's letter tile.
    private static NodeGlyph AppishGlyph(PwNode n) =>
        n.Info.ApplicationId is { Length: > 0 } || n.ApplicationName is { Length: > 0 }
            ? NodeGlyph.App
            : NodeGlyph.Virtual;

    // A USB headset is one device with a sink and a MONO source (its boom mic). A USB audio
    // interface also has both directions, but its input is stereo (seen live: a PCM2900C codec).
    private static bool IsHeadset(PwNode n, PwGraph graph)
    {
        if (n.Info.DeviceBus is "bluetooth") return true;
        if (n.Info.DeviceId is not { } dev || n.Info.DeviceBus != "usb") return false;
        bool sink = false, monoSource = false;
        foreach (var other in graph.Nodes)
        {
            if (other.Info.DeviceId != dev) continue;
            if (other.MediaClass is "Audio/Sink") sink = true;
            else if (other.MediaClass is "Audio/Source" && other.OutputPorts.Count() == 1) monoSource = true;
        }
        return sink && monoSource;
    }

    private static bool Contains(string? s, string token) =>
        s is not null && s.Contains(token, StringComparison.OrdinalIgnoreCase);
}
