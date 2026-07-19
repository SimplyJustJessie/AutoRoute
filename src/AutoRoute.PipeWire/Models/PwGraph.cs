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

    /// <summary>
    /// True when the two snapshots are indistinguishable to every consumer (reconciler, matcher,
    /// board builder): same node ids with the same stable-key props, same ports, and same links
    /// (endpoints + props, which carry the ownership tag). pw-dump output that differs only in
    /// fields the parser drops (params, volumes, link state flips) compares equal, letting
    /// callers skip reconcile/UI work for churn that cannot affect routing.
    /// </summary>
    public bool StructurallyEquals(PwGraph other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (NodesById.Count != other.NodesById.Count
            || PortsById.Count != other.PortsById.Count
            || LinksById.Count != other.LinksById.Count)
            return false;

        foreach (var (id, node) in NodesById)
        {
            if (!other.NodesById.TryGetValue(id, out var o)) return false;
            // Field-wise, not record equality: Ports lists are separate instances per snapshot
            // and are already covered exactly by the PortsById comparison below.
            if (node.NodeName != o.NodeName
                || node.Description != o.Description
                || node.MediaClass != o.MediaClass
                || node.ApplicationName != o.ApplicationName
                || node.ProcessBinary != o.ProcessBinary
                || node.MediaName != o.MediaName)
                return false;
        }

        foreach (var (id, port) in PortsById)
        {
            if (!other.PortsById.TryGetValue(id, out var o) || port != o) return false;
        }

        foreach (var (id, link) in LinksById)
        {
            if (!other.LinksById.TryGetValue(id, out var o)) return false;
            // State is deliberately ignored: nothing reads it, and links flip active/paused every
            // time a stream starts or idles — exactly the churn this comparison exists to absorb.
            if (link.OutNodeId != o.OutNodeId || link.OutPortId != o.OutPortId
                || link.InNodeId != o.InNodeId || link.InPortId != o.InPortId)
                return false;
            if (!PropsEqual(link.Props, o.Props)) return false;
        }
        return true;
    }

    private static bool PropsEqual(
        IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var v) || value != v) return false;
        }
        return true;
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
