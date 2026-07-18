using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Engine;

/// <summary>
/// The idempotent automation core: given the current graph and the policy, create missing
/// managed links, delete stale managed links, and delete links matching an active Suppression —
/// never touching any other unowned link (Protected overrides all). Runs on every GraphUpdated.
/// Wave 2 (Engine) implements the behavior (PLAN "Reconciler").
/// </summary>
public interface IReconciler
{
    /// <summary>Reconcile the live <paramref name="graph"/> against <paramref name="rules"/>.</summary>
    Task ReconcileAsync(PwGraph graph, RulesDocument rules, CancellationToken ct = default);
}
