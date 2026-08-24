using AutoRoute.App.Services;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Tests;

/// <summary>
/// Audio vs. Video classification (<see cref="MediaKind"/>) — the gate that lets the Video tab
/// reuse the exact same board/reconcile plumbing as Audio without either kind leaking into the
/// other's columns/palette.
/// </summary>
public class NodeRolesTests
{
    private static PwNode Node(string mediaClass, PortDirection direction) =>
        new(1, "n", null, mediaClass, null, null, null,
            new[] { new PwPort(1, 1, direction, "p", null, 0) });

    [Fact]
    public void Video_stream_is_video_not_audio()
    {
        var n = Node("Stream/Output/Video", PortDirection.Output);
        Assert.True(NodeRoles.IsVideo(n));
        Assert.False(NodeRoles.IsAudio(n));
        Assert.True(NodeRoles.IsOfKind(n, MediaKind.Video));
        Assert.False(NodeRoles.IsOfKind(n, MediaKind.Audio));
    }

    [Fact]
    public void Audio_stream_is_audio_not_video()
    {
        var n = Node("Stream/Output/Audio", PortDirection.Output);
        Assert.True(NodeRoles.IsAudio(n));
        Assert.False(NodeRoles.IsVideo(n));
        Assert.True(NodeRoles.IsOfKind(n, MediaKind.Audio));
        Assert.False(NodeRoles.IsOfKind(n, MediaKind.Video));
    }

    [Fact]
    public void Video_source_and_target_are_kind_scoped()
    {
        var source = Node("Stream/Output/Video", PortDirection.Output);
        var target = Node("Stream/Input/Video", PortDirection.Input);

        Assert.True(NodeRoles.IsSource(source, MediaKind.Video));
        Assert.False(NodeRoles.IsSource(source, MediaKind.Audio));
        Assert.True(NodeRoles.IsTargetSink(target, MediaKind.Video));
        Assert.False(NodeRoles.IsTargetSink(target, MediaKind.Audio));

        // The default (no-kind) overloads stay Audio-only — every pre-existing call site keeps
        // ignoring video nodes without being touched.
        Assert.False(NodeRoles.IsAudioSource(source));
        Assert.False(NodeRoles.IsTargetSink(target));
    }

    [Fact]
    public void Video_sink_is_monitor_kind_only_for_video()
    {
        var videoSink = Node("Video/Sink", PortDirection.Output);
        Assert.Equal(SourceKind.Monitor, NodeRoles.KindOf(videoSink, MediaKind.Video));
        Assert.True(NodeRoles.IsMonitorSink(videoSink, MediaKind.Video));

        // An Audio/Sink is never mistaken for a Video monitor and vice versa.
        var audioSink = Node("Audio/Sink", PortDirection.Output);
        Assert.False(NodeRoles.IsMonitorSink(audioSink, MediaKind.Video));
    }
}
