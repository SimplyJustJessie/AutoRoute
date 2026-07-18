using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Engine;

/// <summary>
/// The virtual-sink half of the reconcile pass (ADR-0011): makes the world match the declared
/// <see cref="RulesDocument.VirtualSinks"/> — drop-in synced, missing sinks loaded, stale tagged
/// modules unloaded. Runs before link reconcile so a newly created sink's node is available to
/// rules on the following snapshot. Idempotent; failures are logged and self-heal next pass.
/// </summary>
public interface ISinkReconciler
{
    Task EnsureAsync(PwGraph graph, RulesDocument rules, CancellationToken ct = default);
}
