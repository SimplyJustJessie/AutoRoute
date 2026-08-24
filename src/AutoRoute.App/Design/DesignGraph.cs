using System.Collections.Generic;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Design;

/// <summary>
/// A small, deterministic hand-built <see cref="PwGraph"/> mirroring the fixture's shape
/// (Zen fan-out, Spotify, the four null sinks, a capture device, a Discord recStream app-input
/// target, a couple of external unowned Links, and one managed Link). Used for XAML design-time
/// previews and as a fallback when the real fixture can't be located.
/// </summary>
public static class DesignGraph
{
    private static int _port = 1000;

    public static PwGraph Build()
    {
        _port = 1000;
        var nodes = new Dictionary<int, PwNode>();
        var ports = new Dictionary<int, PwPort>();
        var links = new Dictionary<int, PwLink>();

        // --- Target Sinks (null sinks + one hardware sink) ---
        var headset = Sink(nodes, ports, 55, "alsa_output.pro-x-2.analog-stereo", "PRO X 2 Analog Stereo");
        var music = Sink(nodes, ports, 87, "MusicSink", "Music");
        var discord = Sink(nodes, ports, 88, "DiscordSink", "Discord");
        var game = Sink(nodes, ports, 89, "GameSink", "Game");
        Sink(nodes, ports, 90, "DesktopSink", "Desktop");

        // --- App-input target (ephemeral, matched by stable key like a Source) ---
        var recStream = StreamInput(nodes, ports, 300, "recStream", "Discord", "WEBRTC VoiceEngine");

        // --- Sources ---
        var zen1 = AppStream(nodes, ports, 170, "Zen", "Zen", "zen-bin", "Home / X");
        AppStream(nodes, ports, 185, "Zen", "Zen", "zen-bin", "Docs / Y"); // second Zen stream (app granularity)
        var spotify = AppStream(nodes, ports, 135, "spotify", "Spotify", "spotify", "Some Song", rateHz: 44100);
        MonoCapture(nodes, ports, 56, "alsa_input.pro-x-2.mono", "PRO X 2 Mono");
        // Real-world name lengths (seen live): titles/subtitles must ellipsize inside their cards,
        // in the palette AND once assigned into a column — keep this one deliberately obnoxious.
        var codec = MonoCapture(nodes, ports, 57,
            "alsa_input.usb-BurrBrown_from_Texas_Instruments_USB_AUDIO_CODEC-00.analog-stereo",
            "PCM2900C Audio CODEC Analog Stereo (Long Hardware Name)");

        // --- Existing Links: external unowned (render "unsaved") + one managed ---
        Link(links, 501, zen1, headset, unmanaged: true);   // Zen → headset (WirePlumber default)
        Link(links, 502, spotify, music, unmanaged: true);  // Spotify → MusicSink (manual)
        Link(links, 503, zen1, game, unmanaged: false, ruleId: "design-rule-zen-game"); // managed
        Link(links, 504, codec, discord, unmanaged: true);  // long-named capture → a column card

        // --- Video (Spout2PW/OBS shape): one output port, one input port, no channel — mirrors a
        // real capture live (kwin's screencast nodes carry the same single "output_1"/"input_1"
        // shape with no audio.channel prop at all). ---
        var vtubeStudio = AppStreamVideo(nodes, ports, 700, "spout2pw", "spout2pw", "spout2pw", "VTube Studio");
        var obsCam = AppStreamVideo(nodes, ports, 701, "spout2pw", "spout2pw", "spout2pw", "Webcam Overlay");
        var obsCapture = StreamInputVideo(nodes, ports, 710, "OBS Studio", "OBS Studio", "PipeWire Video Capture");
        Link(links, 505, vtubeStudio, obsCapture, unmanaged: false, ruleId: "design-rule-vtube-obs");

        return new PwGraph(nodes, ports, links);
    }

    // ---- node builders -----------------------------------------------------------------

    private static PwNode Sink(IDictionary<int, PwNode> nodes, IDictionary<int, PwPort> ports,
        int id, string name, string desc)
    {
        var list = new List<PwPort>
        {
            P(ports, id, PortDirection.Input, "playback_FL", "FL", 0),
            P(ports, id, PortDirection.Input, "playback_FR", "FR", 1),
            P(ports, id, PortDirection.Output, "monitor_FL", "FL", 2),
            P(ports, id, PortDirection.Output, "monitor_FR", "FR", 3),
        };
        var node = new PwNode(id, name, desc, "Audio/Sink", null, null, null, list,
            new AudioFormat(48000, "F32P"));
        nodes[id] = node;
        return node;
    }

    private static PwNode StreamInput(IDictionary<int, PwNode> nodes, IDictionary<int, PwPort> ports,
        int id, string name, string app, string mediaName)
    {
        var list = new List<PwPort>
        {
            P(ports, id, PortDirection.Input, "input_FL", "FL", 0),
            P(ports, id, PortDirection.Input, "input_FR", "FR", 1),
        };
        var node = new PwNode(id, name, null, "Stream/Input/Audio", app, null, mediaName, list,
            new AudioFormat(48000, "F32LE"));
        nodes[id] = node;
        return node;
    }

    private static PwNode AppStream(IDictionary<int, PwNode> nodes, IDictionary<int, PwPort> ports,
        int id, string name, string app, string binary, string mediaName, int rateHz = 48000)
    {
        var list = new List<PwPort>
        {
            P(ports, id, PortDirection.Output, "output_FL", "FL", 0),
            P(ports, id, PortDirection.Output, "output_FR", "FR", 1),
        };
        var node = new PwNode(id, name, null, "Stream/Output/Audio", app, binary, mediaName, list,
            new AudioFormat(rateHz, "F32LE"));
        nodes[id] = node;
        return node;
    }

    private static PwNode MonoCapture(IDictionary<int, PwNode> nodes, IDictionary<int, PwPort> ports,
        int id, string name, string desc)
    {
        var list = new List<PwPort> { P(ports, id, PortDirection.Output, "capture_MONO", "MONO", 0) };
        var node = new PwNode(id, name, desc, "Audio/Source", null, null, null, list,
            new AudioFormat(48000, "S16LE"));
        nodes[id] = node;
        return node;
    }

    // A video stream/capture node carries one port with no channel and no AudioFormat — the real
    // shape (confirmed live via pw-dump against kwin's screencast nodes and how Spout2PW/OBS's
    // PipeWire Video Capture present).

    private static PwNode AppStreamVideo(IDictionary<int, PwNode> nodes, IDictionary<int, PwPort> ports,
        int id, string name, string app, string binary, string mediaName)
    {
        var list = new List<PwPort> { P(ports, id, PortDirection.Output, "output_1", null, 0) };
        var node = new PwNode(id, name, null, "Stream/Output/Video", app, binary, mediaName, list);
        nodes[id] = node;
        return node;
    }

    private static PwNode StreamInputVideo(IDictionary<int, PwNode> nodes, IDictionary<int, PwPort> ports,
        int id, string name, string app, string mediaName)
    {
        var list = new List<PwPort> { P(ports, id, PortDirection.Input, "input_1", null, 0) };
        var node = new PwNode(id, name, null, "Stream/Input/Video", app, null, mediaName, list);
        nodes[id] = node;
        return node;
    }

    private static PwPort P(IDictionary<int, PwPort> ports, int nodeId, PortDirection dir,
        string portName, string? channel, int index)
    {
        var pid = _port++;
        var port = new PwPort(pid, nodeId, dir, portName, channel, index);
        ports[pid] = port;
        return port;
    }

    private static void Link(IDictionary<int, PwLink> links, int id, PwNode source, PwNode target,
        bool unmanaged, string? ruleId = null)
    {
        // Pair the first output of source to the first input of target (FL), plus FR if present.
        var outs = new List<PwPort>();
        foreach (var p in source.OutputPorts) outs.Add(p);
        var ins = new List<PwPort>();
        foreach (var p in target.InputPorts) ins.Add(p);

        var props = unmanaged
            ? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()
            : new Dictionary<string, string>
            {
                [PwLink.ManagedPropKey] = "true",
                [PwLink.RulePropKey] = ruleId ?? "design-rule",
            };

        var count = System.Math.Min(outs.Count, ins.Count);
        for (var i = 0; i < count; i++)
        {
            var lid = id * 10 + i;
            links[lid] = new PwLink(lid, source.Id, outs[i].Id, target.Id, ins[i].Id, "active", props);
        }
    }
}
