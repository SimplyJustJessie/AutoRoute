using System.Collections.Generic;

namespace AutoRoute.PipeWire.Models;

/// <summary>
/// An explicit port-to-port connection (a Link). Carries <see cref="Props"/> copied verbatim
/// from the pw-dump <c>info.props</c> bag; ownership is read straight off that bag — a Link
/// is AutoRoute-managed iff it carries <c>autoroute.managed = "true"</c> (ADR-0004). This is
/// the sole ownership record in v1; the milestone-1 gate confirms the tag round-trips.
/// </summary>
/// <param name="Id">Numeric object id of the Link.</param>
/// <param name="OutNodeId">Source node id (<c>info["output-node-id"]</c>).</param>
/// <param name="OutPortId">Source output port id (<c>info["output-port-id"]</c>).</param>
/// <param name="InNodeId">Target node id (<c>info["input-node-id"]</c>).</param>
/// <param name="InPortId">Target input port id (<c>info["input-port-id"]</c>).</param>
/// <param name="State">Link state (<c>info.state</c>): active/paused/init/etc. May be null.</param>
/// <param name="Props">Verbatim <c>info.props</c>, scalar values stringified. Never null (empty if absent).</param>
public sealed record PwLink(
    int Id,
    int OutNodeId,
    int OutPortId,
    int InNodeId,
    int InPortId,
    string? State,
    IReadOnlyDictionary<string, string> Props)
{
    /// <summary>The ownership tag key stamped by <c>pw-link -p</c> (ADR-0004).</summary>
    public const string ManagedPropKey = "autoroute.managed";

    /// <summary>The rule-id tag key stamped alongside <see cref="ManagedPropKey"/>.</summary>
    public const string RulePropKey = "autoroute.rule";

    /// <summary>True iff this Link carries our ownership tag — i.e. AutoRoute created it.</summary>
    public bool IsManaged =>
        Props.TryGetValue(ManagedPropKey, out var v) &&
        string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>The rule id that created this managed link, if tagged.</summary>
    public string? RuleId => Props.TryGetValue(RulePropKey, out var v) ? v : null;
}
