using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire;

/// <summary>Per-node volume and mute (app streams, sinks, sources).</summary>
public interface IVolumeController
{
    /// <summary>Set volume on the user-facing cubic scale (1.0 = 100%, the scale wpctl/pavucontrol show).</summary>
    Task SetVolumeAsync(int nodeId, double volume, CancellationToken ct = default);

    Task SetMuteAsync(int nodeId, bool muted, CancellationToken ct = default);
}

/// <summary>
/// Volume via WirePlumber's <c>wpctl</c>, which takes the PipeWire object id directly (no
/// pulse-index translation) and uses the same cubic scale the UI shows. Failures are logged, not
/// thrown: a fader that misses one write is fixed by the next drag or graph refresh.
/// </summary>
public sealed class WpctlVolumeController : IVolumeController
{
    /// <summary>Upper bound the UI allows (100%); wpctl itself would accept overdrive.</summary>
    public const double MaxVolume = 1.0;

    private readonly IProcessRunner _runner;
    private readonly ILogger _log;

    public WpctlVolumeController(IProcessRunner runner, ILogger<WpctlVolumeController>? log = null)
    {
        _runner = runner;
        _log = log ?? NullLogger<WpctlVolumeController>.Instance;
    }

    public Task SetVolumeAsync(int nodeId, double volume, CancellationToken ct = default)
    {
        var v = Math.Clamp(volume, 0, MaxVolume).ToString("0.###", CultureInfo.InvariantCulture);
        return RunAsync(new[] { "set-volume", nodeId.ToString(CultureInfo.InvariantCulture), v }, ct);
    }

    public Task SetMuteAsync(int nodeId, bool muted, CancellationToken ct = default) =>
        RunAsync(new[] { "set-mute", nodeId.ToString(CultureInfo.InvariantCulture), muted ? "1" : "0" }, ct);

    private async Task RunAsync(string[] args, CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync("wpctl", args, throwOnNonZero: false, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                _log.LogWarning("wpctl {Args} failed ({Code}): {Err}", string.Join(' ', args), result.ExitCode, result.StdErr);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wpctl {Args} failed", string.Join(' ', args));
        }
    }
}
