using System;

namespace AutoRoute.PipeWire.Process;

/// <summary>
/// Thrown when a PipeWire CLI tool (pw-dump/pw-link/pactl/…) exits non-zero.
/// Carries the tool name, exit code, and captured stderr for diagnosis.
/// </summary>
public sealed class PwToolException : Exception
{
    public string Tool { get; }
    public string Arguments { get; }
    public int ExitCode { get; }
    public string StdErr { get; }

    public PwToolException(string tool, string arguments, int exitCode, string stdErr)
        : base($"'{tool} {arguments}' exited with code {exitCode}: {stdErr.Trim()}")
    {
        Tool = tool;
        Arguments = arguments;
        ExitCode = exitCode;
        StdErr = stdErr;
    }
}
