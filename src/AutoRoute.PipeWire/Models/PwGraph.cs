using System.Collections.Generic;
using System.Linq;

namespace AutoRoute.PipeWire.Models;

/// <summary>
/// An immutable snapshot of the PipeWire graph: nodes, ports and links with by-id and
/// by-node indices. Produced by <c>PwDumpReader</c>; consumed read-only by the reconciler,
/// linker and UI. Ephemeral ids are valid only within one snapshot.
/// </summary>
public sealed class PwGraph
{
    public IReadOnlyDictionary<int, PwNode> NodesById { get; }
    public IReadOnlyDictionary<int, PwPort> PortsById { get; }
    public IReadOnlyDictionary<int, PwLink> LinksById { get; }

    /// <summary>Ports grouped by their owning node id (by-node index).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<PwPort>> PortsByNodeId { get; }

    public IReadOnlyCollection<PwNode> Nodes => (IReadOnlyCollection<PwNode>)NodesById.Values;
    public IReadOnlyCollection<PwLink> Links => (IReadOnlyCollection<PwLink>)LinksById.Values;
    public IReadOnlyCollection<PwPort> Ports => (IReadOnlyCollection<PwPort>)PortsById.Values;

    /// <summary>An empty graph — the safe last-good default before the first successful dump.</summary>
    public static PwGraph Empty { get; } = new(
        new Dictionary<int, PwNode>(),
        new Dictionary<int, PwPort>(),
        new Dictionary<int, PwLink>());

    public PwGraph(
        IReadOnlyDictionary<int, PwNode> nodesById,
        IReadOnlyDictionary<int, PwPort> portsById,
        IReadOnlyDictionary<int, PwLink> linksById)
    {
        NodesById = nodesById;
        PortsById = portsById;
        LinksById = linksById;
        PortsByNodeId = portsById.Values
            .GroupBy(p => p.NodeId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PwPort>)g.ToList());
    }

    public PwNode? Node(int id) => NodesById.TryGetValue(id, out var n) ? n : null;
    public PwPort? Port(int id) => PortsById.TryGetValue(id, out var p) ? p : null;
    public PwLink? Link(int id) => LinksById.TryGetValue(id, out var l) ? l : null;

    /// <summary>Every Link AutoRoute owns (carries the <c>autoroute.managed</c> tag).</summary>
    public IEnumerable<PwLink> ManagedLinks => Links.Where(l => l.IsManaged);

    /// <summary>Every Link AutoRoute does NOT own — the user's manual/WirePlumber patches.</summary>
    public IEnumerable<PwLink> UnownedLinks => Links.Where(l => !l.IsManaged);

    /// <summary>Links whose output OR input node is the given node id.</summary>
    public IEnumerable<PwLink> LinksForNode(int nodeId) =>
        Links.Where(l => l.OutNodeId == nodeId || l.InNodeId == nodeId);

    /// <summary>Is there already a Link between exactly these two ports?</summary>
    public bool HasLink(int outPortId, int inPortId) =>
        Links.Any(l => l.OutPortId == outPortId && l.InPortId == inPortId);
}
