using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Models;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire;

/// <inheritdoc cref="IPwLinker"/>
public sealed class PwLinker : IPwLinker
{
    public const string Tool = "pw-link";

    private readonly IProcessRunner _runner;
    private readonly ILogger _log;

    public PwLinker(IProcessRunner runner, ILogger<PwLinker>? log = null)
    {
        _runner = runner;
        _log = log ?? NullLogger<PwLinker>.Instance;
    }

    /// <summary>Builds the ownership props JSON stamped on a created link (properly escaped).</summary>
    public static string BuildManagedProps(string ruleId)
    {
        var map = new Dictionary<string, string>
        {
            [PwLink.ManagedPropKey] = "true",
            [PwLink.RulePropKey] = ruleId,
        };
        return JsonSerializer.Serialize(map);
    }

    public async Task<LinkOpResult> ConnectAsync(int outPortId, int inPortId, string ruleId, CancellationToken ct = default)
    {
        var props = BuildManagedProps(ruleId);
        // pw-link [-w] -p PROPS output input   (numeric ids; -w => exit code reflects the attempt)
        var args = new[]
        {
            "-w",
            "-p", props,
            outPortId.ToString(CultureInfo.InvariantCulture),
            inPortId.ToString(CultureInfo.InvariantCulture),
        };

        var result = await _runner.RunAsync(Tool, args, throwOnNonZero: false, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _log.LogInformation("linked {Out}->{In} (rule {Rule})", outPortId, inPortId, ruleId);
            return LinkOpResult.Ok;
        }

        _log.LogWarning("link {Out}->{In} failed (rule {Rule}): {Err}",
            outPortId, inPortId, ruleId, result.StdErr.Trim());
        return LinkOpResult.Fail(result.StdErr.Trim());
    }

    public async Task<LinkOpResult> DisconnectAsync(int linkId, CancellationToken ct = default)
    {
        var args = new[] { "-d", linkId.ToString(CultureInfo.InvariantCulture) };
        return await RunDisconnect(args, $"link {linkId}", ct).ConfigureAwait(false);
    }

    public async Task<LinkOpResult> DisconnectAsync(int outPortId, int inPortId, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-d",
            outPortId.ToString(CultureInfo.InvariantCulture),
            inPortId.ToString(CultureInfo.InvariantCulture),
        };
        return await RunDisconnect(args, $"{outPortId}->{inPortId}", ct).ConfigureAwait(false);
    }

    private async Task<LinkOpResult> RunDisconnect(string[] args, string what, CancellationToken ct)
    {
        var result = await _runner.RunAsync(Tool, args, throwOnNonZero: false, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _log.LogInformation("disconnected {What}", what);
            return LinkOpResult.Ok;
        }

        // A vanished port/link is the expected transient case — log and self-heal next snapshot.
        _log.LogWarning("disconnect {What} failed: {Err}", what, result.StdErr.Trim());
        return LinkOpResult.Fail(result.StdErr.Trim());
    }
}
