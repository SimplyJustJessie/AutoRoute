using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AutoRoute.PipeWire;

/// <summary>
/// A loaded <c>module-null-sink</c> instance as reported by <c>pactl list modules short</c>
/// (<c>index\tname\targs</c>). <paramref name="SinkName"/> is the <c>sink_name=</c> module arg —
/// the stable identity a declared sink is matched by; module indexes are ephemeral and never
/// persisted.
/// </summary>
public sealed record NullSinkModule(int ModuleIndex, string SinkName, string Args)
{
    /// <summary>
    /// True when the module args carry our <c>autoroute.managed=true</c> stamp. Only tagged
    /// modules are ever auto-unloaded (stale cleanup); untagged modules — the user's legacy
    /// static conf, other apps' sinks — are never touched without an explicit user action.
    /// </summary>
    public bool IsAutoRouteTagged => Args.Contains("autoroute.managed=true", System.StringComparison.Ordinal);
}

/// <summary>What to create: <c>sink_name</c>, <c>device.description</c>, and mono vs stereo.</summary>
public sealed record NullSinkRequest(string Name, string Description, bool Mono);

public readonly record struct SinkOpResult(bool Success, int? ModuleIndex, string? Error)
{
    public static SinkOpResult Ok(int? moduleIndex = null) => new(true, moduleIndex, null);
    public static SinkOpResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Runtime lifecycle of virtual (null) sinks via <c>pactl</c> (ADR-0011, hybrid mechanism).
/// Modules load into pipewire-pulse, so created sinks outlive AutoRoute and die only with the
/// sound server — exactly when the generated conf.d drop-in recreates them. Failures are returned
/// as <see cref="SinkOpResult.Fail"/>, never thrown: the reconcile loop retries next snapshot.
/// </summary>
public interface IVirtualSinkController
{
    /// <summary>All loaded <c>module-null-sink</c> modules that expose a parseable <c>sink_name</c>.</summary>
    Task<IReadOnlyList<NullSinkModule>> ListNullSinkModulesAsync(CancellationToken ct = default);

    /// <summary><c>pactl load-module module-null-sink …</c>; returns the printed module index.</summary>
    Task<SinkOpResult> LoadAsync(NullSinkRequest request, CancellationToken ct = default);

    /// <summary><c>pactl unload-module &lt;index&gt;</c>.</summary>
    Task<SinkOpResult> UnloadAsync(int moduleIndex, CancellationToken ct = default);
}
