using System.Text.Json.Serialization;

namespace AutoRoute.Engine.Model;

/// <summary>Channel layout of a declared virtual sink. Full channel-map editing is deferred.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SinkChannels>))]
public enum SinkChannels
{
    Stereo,
    Mono,
}

/// <summary>
/// A declared virtual (null) sink AutoRoute owns end-to-end (ADR-0011, v2): the reconciler keeps a
/// matching <c>module-null-sink</c> loaded at runtime and the generated <c>pipewire-pulse</c> conf.d
/// drop-in recreates it at boot. Ownership is decided by <see cref="Name"/> membership in this
/// declared set — <see cref="Name"/> is both PulseAudio's <c>sink_name</c> and the graph node's
/// <c>node.name</c>.
/// </summary>
public sealed record VirtualSinkSpec(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("channels")] SinkChannels Channels);
