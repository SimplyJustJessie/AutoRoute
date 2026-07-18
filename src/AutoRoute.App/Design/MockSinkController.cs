using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire;

namespace AutoRoute.App.Design;

/// <summary>
/// In-memory <see cref="IVirtualSinkController"/> for the window-free harnesses and the previewer:
/// records loads/unloads and serves them back from the module list, no pactl involved.
/// </summary>
public sealed class MockSinkController : IVirtualSinkController
{
    private readonly List<NullSinkModule> _modules = new();
    private int _nextIndex = 100;

    public IReadOnlyList<NullSinkModule> Modules => _modules;

    public Task<IReadOnlyList<NullSinkModule>> ListNullSinkModulesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NullSinkModule>>(_modules.ToList());

    public Task<SinkOpResult> LoadAsync(NullSinkRequest request, CancellationToken ct = default)
    {
        var index = _nextIndex++;
        _modules.Add(new NullSinkModule(index, request.Name,
            $"sink_name={request.Name} sink_properties=\"device.description='{request.Description}' autoroute.managed=true\""));
        return Task.FromResult(SinkOpResult.Ok(index));
    }

    public Task<SinkOpResult> UnloadAsync(int moduleIndex, CancellationToken ct = default)
    {
        _modules.RemoveAll(m => m.ModuleIndex == moduleIndex);
        return Task.FromResult(SinkOpResult.Ok(moduleIndex));
    }
}
