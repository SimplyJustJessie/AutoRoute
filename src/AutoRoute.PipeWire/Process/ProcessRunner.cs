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
/// Result of a finished CLI invocation whose stdout is kept as raw UTF-8 bytes in a pooled
/// buffer. Exists for pw-dump, whose ~half-megabyte payload as a <see cref="string"/> landed on
/// the Large Object Heap every reload and ratcheted the GC's committed memory up by tens of MB.
/// Dispose returns the buffer to the pool — <see cref="StdOut"/> must not be used afterwards.
/// </summary>
public sealed class ProcessBytesResult : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;
    private readonly bool _pooled;

    public ProcessBytesResult(int exitCode, byte[] buffer, int length, string stdErr, bool pooled)
    {
        ExitCode = exitCode;
        _buffer = buffer;
        _length = length;
        StdErr = stdErr;
        _pooled = pooled;
    }

    public int ExitCode { get; }
    public string StdErr { get; }
    public bool Succeeded => ExitCode == 0;

    /// <summary>Raw stdout bytes; valid only until <see cref="Dispose"/>.</summary>
    public ReadOnlyMemory<byte> StdOut
        => _buffer is { } b ? b.AsMemory(0, _length) : throw new ObjectDisposedException(nameof(ProcessBytesResult));

    public void Dispose()
    {
        var b = _buffer;
        _buffer = null;
        if (b is not null && _pooled)
            System.Buffers.ArrayPool<byte>.Shared.Return(b);
    }
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

    /// <summary>
    /// Like <see cref="RunAsync"/>, but stdout is captured as raw bytes in a pooled buffer
    /// instead of a string. Use for large payloads (pw-dump) that would otherwise allocate on
    /// the Large Object Heap; the caller owns the result and must dispose it.
    /// </summary>
    Task<ProcessBytesResult> RunBytesAsync(
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

    public async Task<ProcessBytesResult> RunBytesAsync(
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

        // stderr stays line-pumped into a string: it is diagnostics, small, and only read on failure.
        var stderr = new StringBuilder();
        var stderrDone = new TaskCompletionSource();
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

        proc.BeginErrorReadLine();

        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var buffer = pool.Rent(512 * 1024);
        var length = 0;
        try
        {
            var stdout = proc.StandardOutput.BaseStream;
            while (true)
            {
                if (length == buffer.Length)
                {
                    var bigger = pool.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, length);
                    pool.Return(buffer);
                    buffer = bigger;
                }
                var read = await stdout.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
                if (read == 0) break; // EOF: the process closed its stdout
                length += read;
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await stderrDone.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pool.Return(buffer);
            TryKill(proc);
            throw;
        }
        catch
        {
            pool.Return(buffer);
            throw;
        }

        if (proc.ExitCode != 0 && throwOnNonZero)
        {
            pool.Return(buffer);
            throw new PwToolException(fileName, string.Join(' ', arguments), proc.ExitCode, stderr.ToString());
        }

        return new ProcessBytesResult(proc.ExitCode, buffer, length, stderr.ToString(), pooled: true);
    }

    private static void TryKill(System.Diagnostics.Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }
}
