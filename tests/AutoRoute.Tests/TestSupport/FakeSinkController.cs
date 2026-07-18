using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire;

namespace AutoRoute.Tests.TestSupport;

/// <summary>
/// Scripted <see cref="IVirtualSinkController"/>: serves a settable module list and records every
/// load/unload, so <c>SinkReconciler</c> logic runs without pactl.
/// </summary>
public sealed class FakeSinkController : IVirtualSinkController
{
    public List<NullSinkModule> Modules { get; } = new();
    public List<NullSinkRequest> Loads { get; } = new();
    public List<int> Unloads { get; } = new();
    public int ListCalls { get; private set; }

    /// <summary>Sink names whose next load should fail (simulates pipewire-pulse being down).</summary>
    public HashSet<string> FailLoads { get; } = new();

    public Task<IReadOnlyList<NullSinkModule>> ListNullSinkModulesAsync(CancellationToken ct = default)
    {
        ListCalls++;
        return Task.FromResult<IReadOnlyList<NullSinkModule>>(Modules.ToList());
    }

    public Task<SinkOpResult> LoadAsync(NullSinkRequest request, CancellationToken ct = default)
    {
        Loads.Add(request);
        if (FailLoads.Contains(request.Name))
            return Task.FromResult(SinkOpResult.Fail("Failure: Module initialization failed"));

        var index = 100 + Loads.Count;
        Modules.Add(new NullSinkModule(index, request.Name,
            $"sink_name={request.Name} sink_properties=\"device.description='{request.Description}' autoroute.managed=true\""));
        return Task.FromResult(SinkOpResult.Ok(index));
    }

    public Task<SinkOpResult> UnloadAsync(int moduleIndex, CancellationToken ct = default)
    {
        Unloads.Add(moduleIndex);
        Modules.RemoveAll(m => m.ModuleIndex == moduleIndex);
        return Task.FromResult(SinkOpResult.Ok(moduleIndex));
    }
}
