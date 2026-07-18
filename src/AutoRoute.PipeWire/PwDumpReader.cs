using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Models;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire;

/// <summary>
/// Runs <c>pw-dump</c> and parses its JSON array into a <see cref="PwGraph"/>.
/// Keeps only <c>Node</c>/<c>Port</c>/<c>Link</c> objects, attaches ports to their owning
/// node via <c>node.id</c>, and captures each Link's <c>info.props</c> verbatim (this is how
/// the <c>autoroute.managed</c> ownership tag is read back — ADR-0004).
///
/// Resilience contract (PLAN "PipeWire layer"):
///  - non-zero exit from pw-dump  → <see cref="PwToolException"/> (surfaced to caller);
///  - malformed JSON              → logged, and <see cref="LastGood"/> is returned unchanged
///                                  (the watcher must never crash on a torn read).
/// </summary>
public sealed class PwDumpReader
{
    public const string Tool = "pw-dump";

    private const string NodeType = "PipeWire:Interface:Node";
    private const string PortType = "PipeWire:Interface:Port";
    private const string LinkType = "PipeWire:Interface:Link";

    private readonly IProcessRunner _runner;
    private readonly ILogger _log;

    /// <summary>The most recent successfully-parsed snapshot; <see cref="PwGraph.Empty"/> until the first success.</summary>
    public PwGraph LastGood { get; private set; } = PwGraph.Empty;

    public PwDumpReader(IProcessRunner runner, ILogger<PwDumpReader>? log = null)
    {
        _runner = runner;
        _log = log ?? NullLogger<PwDumpReader>.Instance;
    }

    /// <summary>
    /// Run pw-dump and return a fresh graph. On malformed JSON, logs and returns
    /// <see cref="LastGood"/> (never throws for a parse error). Propagates
    /// <see cref="PwToolException"/> if pw-dump itself exits non-zero.
    /// </summary>
    public async Task<PwGraph> LoadAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(Tool, Array.Empty<string>(), throwOnNonZero: true, ct)
            .ConfigureAwait(false);

        try
        {
            var graph = Parse(result.StdOut);
            LastGood = graph;
            return graph;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "pw-dump returned malformed JSON; keeping last-good snapshot ({Nodes} nodes)",
                LastGood.NodesById.Count);
            return LastGood;
        }
    }

    /// <summary>
    /// Pure parser: turn a pw-dump JSON payload into a <see cref="PwGraph"/>.
    /// Throws <see cref="JsonException"/> on structurally invalid JSON (callers that must
    /// stay alive should use <see cref="LoadAsync"/>, which swallows that and keeps last-good).
    /// </summary>
    public static PwGraph Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Nodes are built without ports first, then rebuilt once ports are grouped by node.id.
        var nodeShells = new Dictionary<int, PwNode>();
        var ports = new Dictionary<int, PwPort>();
        var links = new Dictionary<int, PwLink>();

        if (root.ValueKind != JsonValueKind.Array)
            throw new JsonException("pw-dump root is not a JSON array");

        foreach (var obj in root.EnumerateArray())
        {
            if (obj.ValueKind != JsonValueKind.Object) continue;
            if (!obj.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                continue;
            if (!TryGetInt(obj, "id", out var id)) continue;

            switch (typeEl.GetString())
            {
                case NodeType:
                    nodeShells[id] = ParseNode(id, obj);
                    break;
                case PortType:
                    var port = ParsePort(id, obj);
                    if (port is not null) ports[id] = port;
                    break;
                case LinkType:
                    var link = ParseLink(id, obj);
                    if (link is not null) links[id] = link;
                    break;
            }
        }

        // Attach ports to their owning node.
        var portsByNode = new Dictionary<int, List<PwPort>>();
        foreach (var p in ports.Values)
        {
            if (!portsByNode.TryGetValue(p.NodeId, out var list))
                portsByNode[p.NodeId] = list = new List<PwPort>();
            list.Add(p);
        }

        var nodes = new Dictionary<int, PwNode>(nodeShells.Count);
        foreach (var (nodeId, shell) in nodeShells)
        {
            var owned = portsByNode.TryGetValue(nodeId, out var list)
                ? (IReadOnlyList<PwPort>)list
                : Array.Empty<PwPort>();
            nodes[nodeId] = shell with { Ports = owned };
        }

        return new PwGraph(nodes, ports, links);
    }

    private static PwNode ParseNode(int id, JsonElement obj)
    {
        var props = GetProps(obj);
        return new PwNode(
            Id: id,
            NodeName: PropString(props, "node.name"),
            Description: PropString(props, "node.description"),
            MediaClass: PropString(props, "media.class"),
            ApplicationName: PropString(props, "application.name"),
            ProcessBinary: PropString(props, "application.process.binary"),
            MediaName: PropString(props, "media.name"),
            Ports: Array.Empty<PwPort>());
    }

    private static PwPort? ParsePort(int id, JsonElement obj)
    {
        if (!obj.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
            return null;

        var direction = ParseDirection(info);
        var props = GetProps(obj);

        // node.id is required to attach the port; skip a port that has none.
        if (props is null || !TryGetInt(props.Value, "node.id", out var nodeId))
            return null;

        TryGetInt(props.Value, "port.id", out var portIndex);

        return new PwPort(
            Id: id,
            NodeId: nodeId,
            Direction: direction,
            PortName: PropString(props, "port.name"),
            Channel: PropString(props, "audio.channel"),
            PortIndex: portIndex);
    }

    private static PwLink? ParseLink(int id, JsonElement obj)
    {
        if (!obj.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
            return null;

        TryGetInt(info, "output-node-id", out var outNode);
        TryGetInt(info, "output-port-id", out var outPort);
        TryGetInt(info, "input-node-id", out var inNode);
        TryGetInt(info, "input-port-id", out var inPort);

        string? state = info.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;

        IReadOnlyDictionary<string, string> props =
            info.TryGetProperty("props", out var p) && p.ValueKind == JsonValueKind.Object
                ? StringifyProps(p)
                : EmptyProps;

        return new PwLink(id, outNode, outPort, inNode, inPort, state, props);
    }

    private static readonly Dictionary<string, string> EmptyProps = new();

    private static PortDirection ParseDirection(JsonElement info)
    {
        if (info.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String)
            return string.Equals(d.GetString(), "output", StringComparison.OrdinalIgnoreCase)
                ? PortDirection.Output
                : PortDirection.Input;
        return PortDirection.Input;
    }

    // ---- prop helpers -------------------------------------------------------

    private static JsonElement? GetProps(JsonElement obj)
        => obj.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
           && info.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object
            ? props
            : (JsonElement?)null;

    private static string? PropString(JsonElement? props, string key)
    {
        if (props is null) return null;
        if (!props.Value.TryGetProperty(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => StripControlChars(el.GetString()),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null, // null / object / array => treat as absent
        };
    }

    /// <summary>
    /// Removes control characters (incl. ESC) from app-controlled strings (application.name,
    /// media.name, …). They are never legitimate in these fields, and left in they would flow
    /// verbatim into logs (terminal escape-sequence injection) and the UI.
    /// </summary>
    private static string? StripControlChars(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        var clean = true;
        foreach (var c in s)
        {
            if (char.IsControl(c)) { clean = false; break; }
        }
        if (clean) return s; // hot path: no allocation for well-behaved names

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> StringifyProps(JsonElement propsObj)
    {
        var map = new Dictionary<string, string>();
        foreach (var prop in propsObj.EnumerateObject())
        {
            var v = prop.Value;
            string? s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Object or JsonValueKind.Array => v.GetRawText(),
                _ => null, // Null / Undefined => skip
            };
            if (s is not null) map[prop.Name] = s;
        }
        return map;
    }

    private static bool TryGetInt(JsonElement obj, string key, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(key, out var el)) return false;
        switch (el.ValueKind)
        {
            case JsonValueKind.Number when el.TryGetInt32(out var n):
                value = n; return true;
            case JsonValueKind.String when int.TryParse(el.GetString(), out var s):
                value = s; return true;
            default:
                return false;
        }
    }
}
