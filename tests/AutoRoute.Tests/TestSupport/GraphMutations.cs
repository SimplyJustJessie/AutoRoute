using System.Collections.Generic;
using System.Linq;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Tests.TestSupport;

/// <summary>
/// Pure helpers to derive new <see cref="PwGraph"/> snapshots from a parsed one — used to simulate
/// "the reconciler already ran" (managed links now present → idempotency) and "the app relaunched"
/// (a node and its ports get fresh ephemeral ids → the flagship reappearance case).
/// </summary>
public static class GraphMutations
{
    /// <summary>
    /// Returns a copy of <paramref name="graph"/> with an <c>autoroute.managed</c> link added for
    /// each (out-port, in-port) pair, tagged with <paramref name="ruleId"/>. New link ids start
    /// above every existing id, mimicking what the reconciler + a fresh dump would produce.
    /// </summary>
    public static PwGraph WithManagedLinks(
        PwGraph graph, IEnumerable<(int outPort, int inPort)> pairs, string ruleId)
    {
        var links = graph.LinksById.ToDictionary(kv => kv.Key, kv => kv.Value);
        var nextId = (graph.LinksById.Count == 0 ? 0 : graph.LinksById.Keys.Max()) + 1000;

        foreach (var (outPort, inPort) in pairs)
        {
            var outNode = graph.Port(outPort)!.NodeId;
            var inNode = graph.Port(inPort)!.NodeId;
            var props = new Dictionary<string, string>
            {
                [PwLink.ManagedPropKey] = "true",
                [PwLink.RulePropKey] = ruleId,
            };
            links[nextId] = new PwLink(nextId, outNode, outPort, inNode, inPort, "active", props);
            nextId++;
        }

        return new PwGraph(
            graph.NodesById.ToDictionary(kv => kv.Key, kv => kv.Value),
            graph.PortsById.ToDictionary(kv => kv.Key, kv => kv.Value),
            links);
    }

    /// <summary>
    /// Returns a copy of <paramref name="graph"/> with a virtual (null) sink node added: an
    /// <c>Audio/Sink</c> with stereo input ports and monitor output ports, exactly the node shape
    /// <c>module-null-sink</c> produces. Ids are allocated above every existing node/port id.
    /// </summary>
    public static PwGraph WithNullSink(PwGraph graph, string name, string? description = null)
    {
        var nodeId = (graph.NodesById.Count == 0 ? 0 : graph.NodesById.Keys.Max()) + 500;
        var portBase = (graph.PortsById.Count == 0 ? 0 : graph.PortsById.Keys.Max()) + 500;

        var ports = new List<PwPort>
        {
            new(portBase + 0, nodeId, PortDirection.Input, "playback_FL", "FL", 0),
            new(portBase + 1, nodeId, PortDirection.Input, "playback_FR", "FR", 1),
            new(portBase + 2, nodeId, PortDirection.Output, "monitor_FL", "FL", 0),
            new(portBase + 3, nodeId, PortDirection.Output, "monitor_FR", "FR", 1),
        };
        var node = new PwNode(
            nodeId, name, description ?? name, "Audio/Sink",
            ApplicationName: null, ProcessBinary: null, MediaName: null, Ports: ports);

        var nodes = graph.NodesById.ToDictionary(kv => kv.Key, kv => kv.Value);
        nodes[nodeId] = node;
        var allPorts = graph.PortsById.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var p in ports) allPorts[p.Id] = p;

        return new PwGraph(nodes, allPorts, graph.LinksById.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    /// <summary>
    /// Returns a copy of <paramref name="graph"/> in which the given nodes — and their ports, and
    /// any link endpoints touching them — have their ids shifted by <paramref name="offset"/>.
    /// Stable-key props (ApplicationName, NodeName, …) are preserved, so a Rule still resolves the
    /// node; only the ephemeral ids change, exactly as a relaunch regenerates them.
    /// </summary>
    public static PwGraph RenumberNodes(PwGraph graph, ISet<int> nodeIds, int offset)
    {
        bool Renumbered(int nodeId) => nodeIds.Contains(nodeId);

        // Ports first: a port owned by a renumbered node gets a new id + new NodeId.
        var newPorts = new Dictionary<int, PwPort>();
        foreach (var p in graph.Ports)
        {
            var np = Renumbered(p.NodeId)
                ? p with { Id = p.Id + offset, NodeId = p.NodeId + offset }
                : p;
            newPorts[np.Id] = np;
        }

        // Nodes: rebuild renumbered nodes with their new ports; keep the rest as-is.
        var newNodes = new Dictionary<int, PwNode>();
        foreach (var n in graph.Nodes)
        {
            if (Renumbered(n.Id))
            {
                var nid = n.Id + offset;
                var ownPorts = newPorts.Values.Where(p => p.NodeId == nid).ToList();
                newNodes[nid] = n with { Id = nid, Ports = ownPorts };
            }
            else
            {
                newNodes[n.Id] = n;
            }
        }

        // Links: shift any endpoint whose node was renumbered.
        var newLinks = new Dictionary<int, PwLink>();
        foreach (var l in graph.Links)
        {
            var nl = l with
            {
                OutNodeId = Renumbered(l.OutNodeId) ? l.OutNodeId + offset : l.OutNodeId,
                OutPortId = Renumbered(l.OutNodeId) ? l.OutPortId + offset : l.OutPortId,
                InNodeId = Renumbered(l.InNodeId) ? l.InNodeId + offset : l.InNodeId,
                InPortId = Renumbered(l.InNodeId) ? l.InPortId + offset : l.InPortId,
            };
            newLinks[nl.Id] = nl;
        }

        return new PwGraph(newNodes, newPorts, newLinks);
    }
}
