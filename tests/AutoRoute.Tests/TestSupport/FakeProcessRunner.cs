using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Process;

namespace AutoRoute.Tests.TestSupport;

/// <summary>
/// A scripted <see cref="IProcessRunner"/> for unit tests: records each invocation and returns
/// queued results (or a default), so linker/reader logic can be exercised without touching the
/// real system.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public sealed record Invocation(string FileName, IReadOnlyList<string> Arguments);

    public List<Invocation> Calls { get; } = new();
    // Each scripted step either returns a result or throws (a binary that can't be started at all,
    // e.g. systemctl absent — which the real ProcessRunner surfaces as a thrown PwToolException).
    private readonly Queue<Func<ProcessResult>> _results = new();

    public FakeProcessRunner Enqueue(ProcessResult result)
    {
        _results.Enqueue(() => result);
        return this;
    }

    public FakeProcessRunner EnqueueStdout(string stdout, int exit = 0)
        => Enqueue(new ProcessResult(exit, stdout, ""));

    public FakeProcessRunner EnqueueFailure(string stderr, int exit = 1)
        => Enqueue(new ProcessResult(exit, "", stderr));

    /// <summary>Script a step where the process itself can't be launched (throws before exiting).</summary>
    public FakeProcessRunner EnqueueThrow(Exception ex)
    {
        _results.Enqueue(() => throw ex);
        return this;
    }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool throwOnNonZero = true,
        CancellationToken ct = default)
    {
        Calls.Add(new Invocation(fileName, arguments));

        var result = _results.Count > 0 ? _results.Dequeue()() : new ProcessResult(0, "", "");
        if (!result.Succeeded && throwOnNonZero)
            throw new PwToolException(fileName, string.Join(' ', arguments), result.ExitCode, result.StdErr);

        return Task.FromResult(result);
    }
}
