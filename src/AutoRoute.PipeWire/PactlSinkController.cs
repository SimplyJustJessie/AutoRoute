using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire;

/// <inheritdoc cref="IVirtualSinkController"/>
public sealed partial class PactlSinkController : IVirtualSinkController
{
    public const string Tool = "pactl";

    private readonly IProcessRunner _runner;
    private readonly ILogger _log;

    public PactlSinkController(IProcessRunner runner, ILogger<PactlSinkController>? log = null)
    {
        _runner = runner;
        _log = log ?? NullLogger<PactlSinkController>.Instance;
    }

    // "index \t name \t args" — args may be empty.
    [GeneratedRegex(@"^\s*(\d+)\t([^\t]+)\t?(.*)$")]
    private static partial Regex ModuleLine();

    [GeneratedRegex(@"(?:^|\s)sink_name=([A-Za-z0-9._-]+)")]
    private static partial Regex SinkNameArg();

    /// <summary>
    /// The module-args tokens for a null sink, shared verbatim by the runtime <c>pactl</c> argv and
    /// the generated conf.d drop-in (single-quoted description inside a double-quoted
    /// <c>sink_properties</c> — the one spelling pipewire-pulse parses on both paths; verified by
    /// the M1 gate script).
    /// </summary>
    public static IReadOnlyList<string> BuildModuleArgs(NullSinkRequest request)
    {
        var args = new List<string> { $"sink_name={request.Name}" };
        if (request.Mono)
        {
            args.Add("channels=1");
            args.Add("channel_map=mono");
        }
        args.Add($"sink_properties=\"device.description='{request.Description}' autoroute.managed=true\"");
        return args;
    }

    public async Task<IReadOnlyList<NullSinkModule>> ListNullSinkModulesAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(Tool, new[] { "list", "modules", "short" }, throwOnNonZero: false, ct)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _log.LogWarning("pactl list modules failed: {Err}", result.StdErr.Trim());
            return Array.Empty<NullSinkModule>();
        }

        var modules = new List<NullSinkModule>();
        foreach (var line in result.StdOut.Split('\n'))
        {
            var m = ModuleLine().Match(line.TrimEnd('\r'));
            if (!m.Success || m.Groups[2].Value != "module-null-sink") continue;

            var args = m.Groups[3].Value;
            var name = SinkNameArg().Match(args);
            if (!name.Success) continue; // no parseable stable identity — leave it alone

            modules.Add(new NullSinkModule(
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                name.Groups[1].Value,
                args));
        }
        return modules;
    }

    public async Task<SinkOpResult> LoadAsync(NullSinkRequest request, CancellationToken ct = default)
    {
        // pactl joins the remaining argv into one module-args string — each token below lands
        // space-separated, quoting intact (ArgumentList, no shell).
        var argv = new List<string> { "load-module", "module-null-sink" };
        argv.AddRange(BuildModuleArgs(request));

        var result = await _runner.RunAsync(Tool, argv, throwOnNonZero: false, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _log.LogWarning("load null sink {Name} failed: {Err}", request.Name, result.StdErr.Trim());
            return SinkOpResult.Fail(result.StdErr.Trim());
        }

        // load-module prints the new module index on stdout.
        if (int.TryParse(result.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            _log.LogInformation("loaded null sink {Name} (module {Index})", request.Name, index);
            return SinkOpResult.Ok(index);
        }

        _log.LogInformation("loaded null sink {Name} (module index unparsed: {Out})",
            request.Name, result.StdOut.Trim());
        return SinkOpResult.Ok();
    }

    public async Task<SinkOpResult> UnloadAsync(int moduleIndex, CancellationToken ct = default)
    {
        var args = new[] { "unload-module", moduleIndex.ToString(CultureInfo.InvariantCulture) };
        var result = await _runner.RunAsync(Tool, args, throwOnNonZero: false, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _log.LogInformation("unloaded module {Index}", moduleIndex);
            return SinkOpResult.Ok(moduleIndex);
        }

        // Module already gone (pipewire-pulse restarted) is the expected transient case.
        _log.LogWarning("unload module {Index} failed: {Err}", moduleIndex, result.StdErr.Trim());
        return SinkOpResult.Fail(result.StdErr.Trim());
    }
}
