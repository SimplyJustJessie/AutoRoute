using System.Linq;
using AutoRoute.App.Services;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Meters;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Tests;

/// <summary>Live level meters: our own meter streams stay off the board, and each node gets the right tap.</summary>
public sealed class LevelMeterTests
{
    // mpv playing into the headset, plus one of our meter streams listening to mpv.
    private const string DumpWithMeter = """
    [
      { "id": 140, "type": "PipeWire:Interface:Node",
        "info": { "state": "running",
          "props": { "node.name": "mpv", "media.class": "Stream/Output/Audio", "application.name": "mpv",
                     "object.serial": 544 },
          "params": { "Props": [ { "channelVolumes": [ 0.125, 0.125 ] } ] } } },
      { "id": 141, "type": "PipeWire:Interface:Port",
        "info": { "direction": "output", "props": { "node.id": 140, "port.name": "output_FL", "port.id": 0 } } },
      { "id": 138, "type": "PipeWire:Interface:Node",
        "info": { "state": "running",
          "props": { "node.name": "AutoRoute", "media.class": "Stream/Input/Audio", "autoroute.meter": 1,
                     "object.serial": 4482 } } },
      { "id": 139, "type": "PipeWire:Interface:Port",
        "info": { "direction": "input", "props": { "node.id": 138, "port.name": "input_MONO", "port.id": 0 } } },
      { "id": 300, "type": "PipeWire:Interface:Link",
        "info": { "output-node-id": 140, "output-port-id": 141, "input-node-id": 138, "input-port-id": 139,
                  "state": "active", "props": {} } }
    ]
    """;

    [Fact]
    public void Own_meter_streams_are_left_out_of_the_graph()
    {
        var g = PwDumpReader.Parse(DumpWithMeter);
        Assert.Null(g.Node(138));
        Assert.DoesNotContain(g.PortsById.Values, p => p.NodeId == 138);
        Assert.Empty(g.LinksById);
        Assert.Equal(544u, g.Node(140)!.Info.Serial);
    }

    private static PwNode Node(string mediaClass, string state, double? volume = 1, uint? serial = null, string name = "n") =>
        new(1, name, null, mediaClass, null, null, null, System.Array.Empty<PwPort>(),
            Meta: new NodeMeta(State: state, Volume: volume, Serial: serial));

    [Fact]
    public void Sinks_tap_their_monitor_before_volume()
    {
        var tap = LevelMeterFeed.TapFor(Node("Audio/Sink", "running", 0.3, name: "Music"));
        Assert.Equal(MeterKey.SinkMonitor("Music"), tap!.Value.Key);
        Assert.Equal(1, tap.Value.Scale);
    }

    [Fact]
    public void App_streams_and_mics_are_measured_after_volume_so_it_is_divided_back_out()
    {
        var app = LevelMeterFeed.TapFor(Node("Stream/Output/Audio", "running", 0.5, serial: 544));
        Assert.Equal(MeterKey.Stream(544), app!.Value.Key);
        Assert.Equal(2, app.Value.Scale, 3);

        var mic = LevelMeterFeed.TapFor(Node("Audio/Source", "running", 0.25, name: "alsa_input.mic"));
        Assert.Equal(MeterKey.Source("alsa_input.mic"), mic!.Value.Key);
        Assert.Equal(4, mic.Value.Scale, 3);
    }

    [Fact]
    public void Idle_nodes_are_metered_up_front_but_recording_streams_never()
    {
        // Passive meters: opening them early never wakes the device, and sound shows instantly.
        Assert.NotNull(LevelMeterFeed.TapFor(Node("Audio/Sink", "suspended")));
        Assert.NotNull(LevelMeterFeed.TapFor(Node("Audio/Source", "idle")));
        Assert.Null(LevelMeterFeed.TapFor(Node("Stream/Input/Audio", "running", serial: 9)));
        Assert.Null(LevelMeterFeed.TapFor(Node("Stream/Output/Audio", "running", serial: null)));
    }
}

public sealed class LevelBarsTests
{
    private static double FaderUnits(double db) => Math.Cbrt(Math.Pow(10, db / 20));

    [Fact]
    public void Quiet_audio_still_shows_but_louder_audio_is_clearly_taller()
    {
        var quiet = AutoRoute.App.Controls.LevelBars.Loudness(FaderUnits(-44));
        var normal = AutoRoute.App.Controls.LevelBars.Loudness(FaderUnits(-24));
        Assert.InRange(quiet, 0.25, 0.35);
        Assert.True(normal > 2 * quiet);
        Assert.Equal(1, AutoRoute.App.Controls.LevelBars.Loudness(FaderUnits(-3)), 3);
        Assert.Equal(0, AutoRoute.App.Controls.LevelBars.Loudness(0));
    }
}
