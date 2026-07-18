using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire.Process;

/// <summary>Result of a finished CLI invocation.</summary>
public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs a short-lived CLI tool to completion, capturing stdout/stderr and exit code.
/// Non-zero exit throws <see cref="PwToolException"/> (unless <c>throwOnNonZero: false</c>).
/// Used for pw-dump / pw-link / pactl. For never-ending processes (pw-mon) use
/// <see cref="LongRunningProcess"/>.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool throwOnNonZero = true,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IProcessRunner"/>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _log;

    public ProcessRunner(ILogger<ProcessRunner>? log = null)
        => _log = log ?? NullLogger<ProcessRunner>.Instance;

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool throwOnNonZero = true,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);

        using var proc = new System.Diagnostics.Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutDone.TrySetResult();
            else stdout.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrDone.TrySetResult();
            else stderr.AppendLine(e.Data);
        };

        _log.LogDebug("exec {File} {Args}", fileName, string.Join(' ', arguments));

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new PwToolException(fileName, string.Join(' ', arguments), -1, ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        // Ensure the async stdout/stderr pumps have flushed their final buffers.
        await Task.WhenAll(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);

        var result = new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());

        if (!result.Succeeded && throwOnNonZero)
            throw new PwToolException(fileName, string.Join(' ', arguments), result.ExitCode, result.StdErr);

        return result;
    }

    private static void TryKill(System.Diagnostics.Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }
}
