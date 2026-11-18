using System;
using System.Collections.Generic;
using System.Linq;
using AutoRoute.App.Services;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Tests;

/// <summary>
/// The redesigned board's naming / glyph / volume rules, against the shapes seen live on a dev box
/// (Logitech PRO X 2 headset, PCM2900C USB codec, Navi 31 HDMI, Ryzen onboard, Easy Effects).
/// </summary>
public sealed class NodePresentationTests
{
    private static int _nextPort = 1000;

    private static PwNode Node(int id, string mediaClass, string? description = null, string? nodeName = null,
        NodeMeta? meta = null, int outPorts = 0, int inPorts = 0, string? app = null, string? media = null)
    {
        var ports = new List<PwPort>();
        for (var i = 0; i < outPorts; i++) ports.Add(new PwPort(_nextPort++, id, PortDirection.Output, $"out_{i}", null, i));
        for (var i = 0; i < inPorts; i++) ports.Add(new PwPort(_nextPort++, id, PortDirection.Input, $"in_{i}", null, i));
        return new PwNode(id, nodeName ?? $"node{id}", description, mediaClass, app, null, media, ports, Meta: meta);
    }

    private static PwGraph Graph(params PwNode[] nodes)
    {
        var dict = nodes.ToDictionary(n => n.Id);
        var ports = nodes.SelectMany(n => n.Ports).ToDictionary(p => p.Id);
        return new PwGraph(dict, ports, new Dictionary<int, PwLink>());
    }

    [Theory]
    [InlineData("PRO X 2 LIGHTSPEED Analog Stereo", "PRO X 2 LIGHTSPEED")]
    [InlineData("PRO X 2 LIGHTSPEED Mono", "PRO X 2 LIGHTSPEED")]
    [InlineData("Ryzen HD Audio Controller Analog Stereo", "Ryzen HD Audio Controller")]
    [InlineData("PCM2900C Audio CODEC Analog Stereo", "PCM2900C Audio CODEC")]
    [InlineData("Some Interface Analog Surround 5.1", "Some Interface")]
    public void Device_names_drop_the_alsa_profile_suffix(string description, string expected)
    {
        var n = Node(1, "Audio/Sink", description, meta: new NodeMeta(DeviceBus: "usb"));
        Assert.Equal(expected, NodePresentation.DeviceName(n));
    }

    [Fact]
    public void Hdmi_uses_its_nick_and_reads_as_gpu()
    {
        var n = Node(1, "Audio/Sink", "Navi 31 HDMI/DP Audio Digital Stereo (HDMI)",
            meta: new NodeMeta(Nick: "HDMI 0", DeviceBus: "pci"), inPorts: 2);
        var look = NodePresentation.ForTarget(n, Graph(n), "HDMI 0", MediaKind.Audio);
        Assert.Equal("HDMI 0", NodePresentation.DeviceName(n));
        Assert.Equal("GPU", look.TypeLine);
        Assert.Equal(NodeGlyph.Monitor, look.Glyph);
        Assert.Equal((int)TargetGroup.Device, look.Group);
    }

    [Fact]
    public void Non_device_nodes_keep_their_own_title()
    {
        var n = Node(1, "Audio/Sink", "Discord", nodeName: "Discord", inPorts: 2);
        Assert.Null(NodePresentation.DeviceName(n));
        var look = NodePresentation.ForTarget(n, Graph(n), "Discord", MediaKind.Audio);
        Assert.Equal((int)TargetGroup.Virtual, look.Group);
        Assert.Equal(NodeGlyph.Virtual, look.Glyph);
        Assert.Equal(string.Empty, look.TypeLine); // the "Virtual" header already says it
    }

    [Fact]
    public void Usb_device_with_a_mono_mic_is_a_headset_but_a_stereo_interface_is_not()
    {
        var headsetSink = Node(1, "Audio/Sink", "PRO X 2 LIGHTSPEED Analog Stereo", meta: new NodeMeta(DeviceBus: "usb", DeviceId: 40), inPorts: 2);
        var headsetMic = Node(2, "Audio/Source", "PRO X 2 LIGHTSPEED Mono", meta: new NodeMeta(DeviceBus: "usb", DeviceId: 40), outPorts: 1);
        var codecSink = Node(3, "Audio/Sink", "PCM2900C Audio CODEC Analog Stereo", meta: new NodeMeta(DeviceBus: "usb", DeviceId: 41), inPorts: 2);
        var codecIn = Node(4, "Audio/Source", "PCM2900C Audio CODEC Analog Stereo", meta: new NodeMeta(DeviceBus: "usb", DeviceId: 41), outPorts: 2);
        var g = Graph(headsetSink, headsetMic, codecSink, codecIn);

        Assert.Equal(NodeGlyph.Headset, NodePresentation.ForTarget(headsetSink, g, "PRO X 2", MediaKind.Audio).Glyph);
        // The boom mic draws as a mic, so as a chip it can't pass for the headphones.
        Assert.Equal(NodeGlyph.Mic, NodePresentation.ForSource(headsetMic, g, "PRO X 2", SourceKind.Capture, MediaKind.Audio).Glyph);
        Assert.Equal(NodeGlyph.Interface, NodePresentation.ForTarget(codecSink, g, "PCM2900C", MediaKind.Audio).Glyph);

        var mic = NodePresentation.ForSource(headsetMic, g, "PRO X 2", SourceKind.Capture, MediaKind.Audio);
        Assert.True(mic.IsInput);
        Assert.Equal("USB", mic.TypeLine);

        // ALSA names both sides of the hardware alike; the input side says which it is.
        Assert.Equal("PRO X 2 LIGHTSPEED mic", NodePresentation.SourceName(headsetMic, g));
        Assert.Equal("PCM2900C Audio CODEC input", NodePresentation.SourceName(codecIn, g));
        Assert.Equal("PRO X 2 LIGHTSPEED", NodeRoles.TargetTitle(headsetSink));
    }

    [Fact]
    public void App_streams_hide_generic_media_names()
    {
        var legcord = Node(1, "Stream/Output/Audio", app: "legcord", media: "Playback", outPorts: 2);
        var mpv = Node(2, "Stream/Output/Audio", app: "mpv", media: "radio-mpv", outPorts: 2);
        var g = Graph(legcord, mpv);
        Assert.Equal(string.Empty, NodePresentation.ForSource(legcord, g, "legcord", SourceKind.AppStream, MediaKind.Audio).TypeLine);
        Assert.Equal("radio-mpv", NodePresentation.ForSource(mpv, g, "mpv", SourceKind.AppStream, MediaKind.Audio).TypeLine);
    }

    [Fact]
    public void Virtual_source_target_carries_the_mic_badge()
    {
        var ee = Node(1, "Audio/Source/Virtual", "Easy Effects Source", "easyeffects_source",
            new NodeMeta(ApplicationId: "com.github.wwmm.easyeffects", IsVirtual: true), inPorts: 2, outPorts: 2);
        var look = NodePresentation.ForTarget(ee, Graph(ee), "Easy Effects Source", MediaKind.Audio);
        Assert.True(look.IsInput);
        Assert.Equal(NodeGlyph.App, look.Glyph);
        Assert.Equal("EE", look.Initials);
    }

    [Fact]
    public void Tile_color_is_stable_per_identity()
    {
        Assert.Equal(NodePresentation.ColorFor("legcord"), NodePresentation.ColorFor("legcord"));
        Assert.Equal(NodePresentation.ColorFor("legcord"), NodePresentation.ColorFor("LEGCORD"));
    }

    private const string DumpWithVolume = """
    [
      { "id": 54, "type": "PipeWire:Interface:Node",
        "info": { "state": "suspended",
          "props": { "node.name": "alsa_output.hdmi", "node.description": "Navi 31 HDMI/DP Audio Digital Stereo (HDMI)",
                     "media.class": "Audio/Sink", "node.nick": "HDMI 0", "device.bus": "pci", "device.id": 42 },
          "params": { "Props": [ { "volume": 1.0, "mute": false, "channelVolumes": [ 0.063997, 0.063997 ] } ] } } },
      { "id": 55, "type": "PipeWire:Interface:Node",
        "info": { "state": "running",
          "props": { "node.name": "mpv", "media.class": "Stream/Output/Audio", "application.name": "mpv" },
          "params": { "Props": [ { "channelVolumes": [ 0.125, 0.216 ], "mute": true } ] } } }
    ]
    """;

    [Fact]
    public void Dump_parses_cubic_volume_mute_state_and_device_props()
    {
        var g = PwDumpReader.Parse(DumpWithVolume);
        var hdmi = g.Node(54)!.Info;
        Assert.Equal(0.40, hdmi.Volume!.Value, 2);  // cbrt(0.064): what wpctl shows
        Assert.False(hdmi.Muted);
        Assert.Equal("HDMI 0", hdmi.Nick);
        Assert.Equal("pci", hdmi.DeviceBus);
        Assert.Equal(42, hdmi.DeviceId);
        Assert.False(hdmi.IsRunning);

        var mpv = g.Node(55)!.Info;
        Assert.Equal(0.60, mpv.Volume!.Value, 2);   // loudest channel: cbrt(0.216)
        Assert.True(mpv.Muted);
        Assert.True(mpv.IsRunning);
    }

    [Fact]
    public void Volume_only_change_is_structurally_equal_but_not_presentation_equal()
    {
        var a = PwDumpReader.Parse(DumpWithVolume);
        var b = PwDumpReader.Parse(DumpWithVolume.Replace("0.063997, 0.063997", "0.5, 0.5"));
        Assert.True(a.StructurallyEquals(b));
        Assert.False(a.PresentationEquals(b));
        Assert.True(a.PresentationEquals(PwDumpReader.Parse(DumpWithVolume)));
    }
}
