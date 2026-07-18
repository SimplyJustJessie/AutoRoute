using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.App.Design;

/// <summary>
/// No-op <see cref="IReconciler"/> for standalone UI development. In the real app the reconciler
/// creates the managed Links a Rule implies; here the board optimistically calls the linker and
/// renders managed cards straight from the policy. Wave 3 supplies the real <c>Reconciler</c>.
/// </summary>
public sealed class MockReconciler : IReconciler
{
    public Task ReconcileAsync(PwGraph graph, RulesDocument rules, CancellationToken ct = default) =>
        Task.CompletedTask;
}
