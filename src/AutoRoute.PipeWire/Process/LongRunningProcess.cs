using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.PipeWire.Process;

/// <summary>
/// Drives a never-ending CLI process (e.g. <c>pw-mon</c>), pumping each stdout line to
/// <see cref="LineReceived"/> and signalling termination via <see cref="Exited"/>.
/// The owner (PwMonMonitor) is responsible for respawn/backoff. Disposal kills the process.
/// </summary>
public sealed class LongRunningProcess : IAsyncDisposable
{
    // Every live child, so none can outlive us when the orderly DisposeAsync never runs: the shutdown
    // watchdog's Environment.Exit, or a crash (an unhandled exception skips host disposal). Without
    // this a dead parent leaves pw-mon running, and under the XDG-autostart unit (ExitType=cgroup)
    // that lone orphan even keeps the service "active". PR_SET_PDEATHSIG isn't used: it fires when
    // the *thread* that forked exits, and .NET forks from thread-pool threads that retire, so it
    // would kill healthy children. Only SIGKILL / native faults still escape this net.
    private static readonly ConcurrentDictionary<LongRunningProcess, byte> Live = new();
    private static int _exitHooksInstalled;

    private readonly string _fileName;
    private readonly string[] _arguments;
    private readonly ILogger _log;
    private System.Diagnostics.Process? _proc;
    private Task? _pump;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised for every line the process writes to stdout.</summary>
    public event Action<string>? LineReceived;

    /// <summary>Raised once when the process exits (argument is the exit code, or -1 if unknown).</summary>
    public event Action<int>? Exited;

    public LongRunningProcess(string fileName, string[] arguments, ILogger? log = null)
    {
        _fileName = fileName;
        _arguments = arguments;
        _log = log ?? NullLogger.Instance;
    }

    public bool IsRunning => _proc is { HasExited: false };

    public void Start()
    {
        if (_proc is not null)
            throw new InvalidOperationException("Process already started.");

        var psi = new ProcessStartInfo
        {
            FileName = _fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in _arguments)
            psi.ArgumentList.Add(a);

        _proc = new System.Diagnostics.Process { StartInfo = psi };
        _log.LogDebug("spawn long-running {File} {Args}", _fileName, string.Join(' ', _arguments));
        InstallExitHooks();
        _proc.Start();
        Live.TryAdd(this, 0);

        _pump = Task.Run(() => PumpAsync(_proc, _cts.Token));
    }

    /// <summary>Number of started, not-yet-disposed instances (diagnostics/tests).</summary>
    public static int LiveCount => Live.Count;

    /// <summary>
    /// Synchronously kill every live child. Runs from <see cref="AppDomain.ProcessExit"/> and
    /// <see cref="AppDomain.UnhandledException"/>; safe to call more than once, from any thread.
    /// </summary>
    public static void KillAllLive()
    {
        foreach (var lrp in Live.Keys)
            lrp.KillNoThrow();
    }

    private static void InstallExitHooks()
    {
        if (Interlocked.Exchange(ref _exitHooksInstalled, 1) == 1) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAllLive();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => KillAllLive();
    }

    private void KillNoThrow()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); }
        catch { /* best effort: already gone or disposed */ }
    }

    private async Task PumpAsync(System.Diagnostics.Process proc, CancellationToken ct)
    {
        int exitCode = -1;
        try
        {
            var reader = proc.StandardOutput;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break; // EOF => process ended its stdout
                try { LineReceived?.Invoke(line); }
                catch (Exception ex) { _log.LogWarning(ex, "LineReceived handler threw"); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "long-running stdout pump failed for {File}", _fileName);
        }
        finally
        {
            try { if (proc.HasExited) exitCode = proc.ExitCode; } catch { /* ignore */ }
            if (!ct.IsCancellationRequested)
                Exited?.Invoke(exitCode);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }

        KillNoThrow();
        Live.TryRemove(this, out _);

        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); } catch { /* ignore */ }
        }

        _proc?.Dispose();
        _cts.Dispose();
    }
}
