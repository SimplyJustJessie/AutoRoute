using System.Linq;
using System.Threading.Tasks;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

/// <summary>
/// Parses the REAL captured pw-dump from this machine and asserts the graph shape, port
/// attachment, link endpoints, fan-out, and (critically) that link props are captured verbatim —
/// which is what makes the ADR-0004 ownership tag readable.
/// </summary>
public class PwDumpReaderTests
{
    private static PwGraph RealGraph() => PwDumpReader.Parse(Fixtures.PwDumpSampleJson);

    [Fact]
    public void Parses_expected_node_port_link_counts()
    {
        var g = RealGraph();
        Assert.Equal(31, g.NodesById.Count);
        Assert.Equal(81, g.PortsById.Count);
        Assert.Equal(14, g.LinksById.Count);
    }

    [Fact]
    public void Attaches_ports_to_owning_node_by_node_id()
    {
        var g = RealGraph();

        // Zen (170) is an app stream: two output ports FL/FR, no inputs => a draggable Source.
        var zen = g.Node(170)!;
        Assert.Equal("Zen", zen.NodeName);
        Assert.Equal("Zen", zen.ApplicationName);
        Assert.Equal("zen-bin", zen.ProcessBinary);
        Assert.Equal("Home / X", zen.MediaName);
        Assert.Equal("Stream/Output/Audio", zen.MediaClass);
        Assert.True(zen.IsDraggableSource);
        Assert.False(zen.IsDropTarget);

        var outChannels = zen.OutputPorts.Select(p => p.Channel).OrderBy(c => c).ToArray();
        Assert.Equal(new[] { "FL", "FR" }, outChannels);
        Assert.Equal(174, zen.OutputPorts.Single(p => p.Channel == "FL").Id);
        Assert.Equal(175, zen.OutputPorts.Single(p => p.Channel == "FR").Id);
    }

    [Fact]
    public void Sink_has_input_ports_and_monitor_output_ports()
    {
        var g = RealGraph();
        var gameSink = g.Node(89)!;
        Assert.Equal("GameSink", gameSink.NodeName);
        Assert.Equal("Audio/Sink", gameSink.MediaClass);
        // A sink is a drop target (playback inputs) AND a source via its monitor (outputs).
        Assert.True(gameSink.IsDropTarget);
        Assert.True(gameSink.IsDraggableSource);
        Assert.Equal(2, gameSink.InputPorts.Count());
        Assert.Equal(2, gameSink.OutputPorts.Count());
    }

    [Fact]
    public void Tolerates_nodes_with_null_media_class()
    {
        // MIDI/DSP nodes (e.g. Dummy-Driver, ee_* DSP) have null media.class and must not crash.
        var g = RealGraph();
        var dummy = g.Node(29)!;
        Assert.Null(dummy.MediaClass);
        Assert.Equal("Dummy-Driver", dummy.NodeName);
    }

    [Fact]
    public void Captures_link_endpoints_and_props()
    {
        var g = RealGraph();

        // Link 172: 170:174 -> 55:65, active, with a non-empty props bag.
        var link = g.Link(172)!;
        Assert.Equal(170, link.OutNodeId);
        Assert.Equal(174, link.OutPortId);
        Assert.Equal(55, link.InNodeId);
        Assert.Equal(65, link.InPortId);
        Assert.Equal("active", link.State);
        Assert.NotEmpty(link.Props);
        // None of the pre-existing links are AutoRoute-owned (no tag yet).
        Assert.False(link.IsManaged);
    }

    [Fact]
    public void Captures_spotify_fan_out_from_one_output_port()
    {
        var g = RealGraph();
        // Spotify output FL (port 190) is linked to TWO targets at once (nodes 59 and 87).
        var fromFl = g.Links.Where(l => l.OutPortId == 190).Select(l => l.InNodeId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 59, 87 }, fromFl);
    }

    [Fact]
    public void ManagedLinks_and_UnownedLinks_partition_by_tag()
    {
        var g = RealGraph();
        Assert.Empty(g.ManagedLinks);
        Assert.Equal(g.LinksById.Count, g.UnownedLinks.Count());
    }

    [Fact]
    public void Reads_negotiated_format_from_active_stream()
    {
        var g = RealGraph();

        // Zen (170) is live: its info.params.Format is fixated at 48 kHz / F32LE.
        var zen = g.Node(170)!.Format!;
        Assert.Equal(48000, zen.SampleRateHz);
        Assert.Equal("F32LE", zen.SampleFormat);
        Assert.Equal("48 kHz · 32-bit float", zen.Summary);

        // spotify (135) runs at 44.1 kHz — the odd rate must format as "44.1 kHz", not "44 kHz".
        var spotify = g.Node(135)!.Format!;
        Assert.Equal(44100, spotify.SampleRateHz);
        Assert.Equal("44.1 kHz · 32-bit float", spotify.Summary);

        // A hardware sink with a concrete negotiated Format (55) — S16LE => 16-bit (no "float").
        var headset = g.Node(55)!.Format!;
        Assert.Equal(48000, headset.SampleRateHz);
        Assert.Equal("S16LE", headset.SampleFormat);
        Assert.Equal("48 kHz · 16-bit", headset.Summary);
    }

    [Fact]
    public void Falls_back_to_EnumFormat_default_for_idle_sink()
    {
        var g = RealGraph();

        // GameSink (89) is idle: info.params.Format is empty, so the advertised EnumFormat
        // default (F32P @ 48000) is what we surface.
        var game = g.Node(89)!.Format!;
        Assert.Equal(48000, game.SampleRateHz);
        Assert.Equal("F32P", game.SampleFormat);
        Assert.Equal("48 kHz · 32-bit float", game.Summary);
    }

    [Fact]
    public void No_audio_format_for_dsp_or_video_nodes()
    {
        var g = RealGraph();

        // MIDI/DSP nodes carry no audio params.
        Assert.Null(g.Node(29)!.Format);   // Dummy-Driver

        // A video stream's EnumFormat is mediaType=video — it must never be read as a PCM format.
        Assert.Null(g.Node(198)!.Format);  // kwin_wayland (Stream/Output/Video)
    }
}
