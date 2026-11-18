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
        // Bytes, not a string: the payload is ~half a megabyte on a real desktop, and a string
        // that size lands on the Large Object Heap on every reload. The pooled buffer lives
        // exactly as long as the parse.
        using var result = await _runner.RunBytesAsync(Tool, Array.Empty<string>(), throwOnNonZero: true, ct)
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
    /// Convenience overload for tests/fixtures; the hot path is the UTF-8 overload.
    /// </summary>
    public static PwGraph Parse(string json)
        => Parse(System.Text.Encoding.UTF8.GetBytes(json));

    /// <inheritdoc cref="Parse(string)"/>
    public static PwGraph Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json);
        var root = doc.RootElement;

        // Nodes are built without ports first, then rebuilt once ports are grouped by node.id.
        var nodeShells = new Dictionary<int, PwNode>();
        var ports = new Dictionary<int, PwPort>();
        var links = new Dictionary<int, PwLink>();
        var hidden = new HashSet<int>(); // our own level-meter streams (see PulseLevelMeter)

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
                    if (IsOwnMeter(obj)) hidden.Add(id);
                    else nodeShells[id] = ParseNode(id, obj);
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

        // Our meter streams stay invisible: no node, no ports, no links, so the board never shows
        // them and the reconciler never sees their links as something to clean up.
        if (hidden.Count > 0)
        {
            foreach (var (pid, p) in ports.ToArray())
                if (hidden.Contains(p.NodeId)) ports.Remove(pid);
            foreach (var (lid, l) in links.ToArray())
                if (hidden.Contains(l.OutNodeId) || hidden.Contains(l.InNodeId)) links.Remove(lid);
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

    private static bool IsOwnMeter(JsonElement obj) =>
        GetProps(obj) is { } props && props.TryGetProperty(Meters.PulseLevelMeter.MeterProperty, out _);

    private static PwNode ParseNode(int id, JsonElement obj)
    {
        var props = GetProps(obj);
        AudioFormat? format = null;
        NodeMeta? meta = null;
        if (obj.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            format = ParseAudioFormat(info);
            meta = ParseMeta(props, info);
        }
        return new PwNode(
            Id: id,
            NodeName: PropString(props, "node.name"),
            Description: PropString(props, "node.description"),
            MediaClass: PropString(props, "media.class"),
            ApplicationName: PropString(props, "application.name"),
            ProcessBinary: PropString(props, "application.process.binary"),
            MediaName: PropString(props, "media.name"),
            Ports: Array.Empty<PwPort>(),
            Format: format,
            Meta: meta);
    }

    /// <summary>Presentation/control metadata: device bus + nick, run state, volume and mute.</summary>
    private static NodeMeta ParseMeta(JsonElement? props, JsonElement info)
    {
        var state = info.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;

        double? volume = null;
        bool? muted = null;
        if (info.TryGetProperty("params", out var prms) && prms.ValueKind == JsonValueKind.Object
            && prms.TryGetProperty("Props", out var propsArr) && propsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in propsArr.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (volume is null && entry.TryGetProperty("channelVolumes", out var cv)
                    && cv.ValueKind == JsonValueKind.Array)
                {
                    double max = -1;
                    foreach (var v in cv.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d > max)
                            max = d;
                    // Props hold linear gain; users (wpctl, pavucontrol) see the cube root.
                    if (max >= 0) volume = Math.Cbrt(max);
                }
                if (muted is null && entry.TryGetProperty("mute", out var mu)
                    && mu.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    muted = mu.GetBoolean();
            }
        }

        uint? serial = props is { } pr && TryGetInt(pr, "object.serial", out var ser) && ser >= 0 ? (uint)ser : null;

        var monitorVolume = props is { } pv && pv.TryGetProperty("monitor.channel-volumes", out var mcv)
            && (mcv.ValueKind == JsonValueKind.True
                || (mcv.ValueKind == JsonValueKind.String && mcv.GetString() == "true"));

        return new NodeMeta(
            MonitorHasVolume: monitorVolume,
            Serial: serial,
            Nick: PropString(props, "node.nick"),
            DeviceBus: PropString(props, "device.bus"),
            DeviceId: int.TryParse(PropString(props, "device.id"), out var did) ? did : null,
            IsVirtual: PropString(props, "node.virtual") == "true",
            ApplicationId: PropString(props, "application.id"),
            State: state,
            Volume: volume,
            Muted: muted);
    }

    /// <summary>
    /// Read a node's sample rate + format from <c>info.params</c>. Prefers the negotiated
    /// <c>Format</c> (present while a stream is active); falls back to the default of the advertised
    /// <c>EnumFormat</c> for an idle device. Only <c>audio/raw</c> entries are considered, so video
    /// streams and iec958 passthrough never masquerade as a PCM format. Null when neither yields
    /// a rate or a format token.
    /// </summary>
    private static AudioFormat? ParseAudioFormat(JsonElement info)
    {
        if (!info.TryGetProperty("params", out var prms) || prms.ValueKind != JsonValueKind.Object)
            return null;
        return ExtractAudioFormat(prms, "Format") ?? ExtractAudioFormat(prms, "EnumFormat");
    }

    private static AudioFormat? ExtractAudioFormat(JsonElement prms, string key)
    {
        if (!prms.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!IsAudioRaw(entry)) continue;

            var rate = ScalarOrDefaultInt(entry, "rate");
            var format = ScalarOrDefaultString(entry, "format");
            if (rate is null && format is null) continue;
            return new AudioFormat(rate, format);
        }
        return null;
    }

    /// <summary>An <c>info.params</c> Format/EnumFormat entry describing raw PCM audio.</summary>
    private static bool IsAudioRaw(JsonElement entry) =>
        StringEquals(entry, "mediaType", "audio") && StringEquals(entry, "mediaSubtype", "raw");

    private static bool StringEquals(JsonElement obj, string key, string value) =>
        obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
        && string.Equals(el.GetString(), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A params field is either a concrete scalar (a fixated Format) or a
    /// <c>{ "default": …, "min": …, "max": … }</c> choice (an EnumFormat range). Read the scalar,
    /// or the choice's default.
    /// </summary>
    private static int? ScalarOrDefaultInt(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("default", out var d)
            && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var dn)) return dn;
        return null;
    }

    private static string? ScalarOrDefaultString(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("default", out var d)
            && d.ValueKind == JsonValueKind.String) return d.GetString();
        return null;
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
