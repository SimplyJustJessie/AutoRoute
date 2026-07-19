using System.Collections.Generic;
using System.Linq;

namespace AutoRoute.PipeWire.Models;

/// <summary>
/// A PipeWire graph node — an app stream, a capture device, a sink, or a sink's monitor.
/// The stable-key match fields (<see cref="ApplicationName"/>, <see cref="NodeName"/>,
/// <see cref="ProcessBinary"/>, <see cref="MediaName"/>, <see cref="MediaClass"/>) are what
/// Rules/Suppressions/Protected match against; <see cref="Id"/> is ephemeral (regenerated
/// every launch) and must never appear in a persisted rule.
/// </summary>
/// <param name="Id">Numeric object id of the Node (ephemeral).</param>
/// <param name="NodeName"><c>node.name</c> (e.g. <c>GameSink</c>, <c>Zen</c>). May be null.</param>
/// <param name="Description"><c>node.description</c> (friendly label). May be null.</param>
/// <param name="MediaClass"><c>media.class</c> (e.g. <c>Stream/Output/Audio</c>, <c>Audio/Sink</c>). Null for MIDI/DSP nodes.</param>
/// <param name="ApplicationName"><c>application.name</c> (e.g. <c>Zen</c>, <c>Discord</c>). May be null.</param>
/// <param name="ProcessBinary"><c>application.process.binary</c> (e.g. <c>zen-bin</c>). May be null.</param>
/// <param name="MediaName"><c>media.name</c> (e.g. <c>Home / X</c>). May be null.</param>
/// <param name="Ports">Ports owned by this node (attached by <c>node.id</c> at parse time).</param>
/// <param name="Format">Sample rate + bit depth the node runs at (from <c>info.params</c>). Null when the node carries no audio format (MIDI/DSP/video) or none was advertised.</param>
public sealed record PwNode(
    int Id,
    string? NodeName,
    string? Description,
    string? MediaClass,
    string? ApplicationName,
    string? ProcessBinary,
    string? MediaName,
    IReadOnlyList<PwPort> Ports,
    AudioFormat? Format = null)
{
    public IEnumerable<PwPort> OutputPorts => Ports.Where(p => p.IsOutput);
    public IEnumerable<PwPort> InputPorts => Ports.Where(p => p.IsInput);

    public bool HasOutputPorts => Ports.Any(p => p.IsOutput);
    public bool HasInputPorts => Ports.Any(p => p.IsInput);

    /// <summary>True when the node exposes output ports, i.e. it can be dragged FROM as a Source.</summary>
    public bool IsDraggableSource => HasOutputPorts;

    /// <summary>True when the node exposes input ports, i.e. a Source can be dropped ONTO it as a Target Sink.</summary>
    public bool IsDropTarget => HasInputPorts;
}
