using System;
using System.Collections.Generic;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Services;

/// <summary>
/// Builds Engine model records (<see cref="Rule"/>, <see cref="Suppression"/>,
/// <see cref="ProtectedMatch"/>) from live nodes, using the most specific available stable key.
/// Everything is app-granularity: a Source prefers <c>application.name</c>, a Target prefers
/// <c>node.name</c> (sinks are named), so the pair survives the nodes' ephemeral reincarnations.
/// </summary>
public static class RuleFactory
{
    public static MatchCriteria SourceCriteria(PwNode source)
    {
        if (Has(source.ApplicationName)) return One(Field.ApplicationName, source.ApplicationName!);
        if (Has(source.NodeName)) return One(Field.NodeName, source.NodeName!);
        if (Has(source.MediaName)) return One(Field.MediaName, source.MediaName!);
        return MatchCriteria.Empty;
    }

    public static MatchCriteria TargetCriteria(PwNode target)
    {
        if (Has(target.NodeName)) return One(Field.NodeName, target.NodeName!);
        if (Has(target.ApplicationName)) return One(Field.ApplicationName, target.ApplicationName!);
        if (Has(target.MediaName)) return One(Field.MediaName, target.MediaName!);
        return MatchCriteria.Empty;
    }

    /// <summary>Protection matches a node/app; prefer the app key so it covers all of an app's streams.</summary>
    public static MatchCriteria ProtectCriteria(PwNode node)
    {
        if (Has(node.ApplicationName)) return One(Field.ApplicationName, node.ApplicationName!);
        if (Has(node.NodeName)) return One(Field.NodeName, node.NodeName!);
        if (Has(node.MediaName)) return One(Field.MediaName, node.MediaName!);
        return MatchCriteria.Empty;
    }

    public static Rule PositiveRule(PwNode source, PwNode target) => new(
        Id: NewId(),
        Name: $"{NodeRoles.SourceTitle(source)} → {NodeRoles.TargetTitle(target)}",
        Enabled: true,
        Source: SourceCriteria(source),
        Target: TargetCriteria(target));

    public static Suppression SuppressionFor(PwNode source, PwNode target) => new(
        Id: NewId(),
        Source: SourceCriteria(source),
        Target: TargetCriteria(target));

    public static ProtectedMatch ProtectedFor(PwNode node) => new(
        Id: NewId(),
        Match: ProtectCriteria(node));

    public static string NewId() => Guid.NewGuid().ToString("n");

    private static MatchCriteria One(Field field, string value) =>
        new(new List<Predicate> { new(field, Op.Equals, value) });

    private static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);
}
