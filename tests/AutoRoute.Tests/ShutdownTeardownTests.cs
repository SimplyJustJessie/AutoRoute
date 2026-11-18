using System;
using System.Threading.Tasks;
using AutoRoute.App.Hosting;
using AutoRoute.PipeWire.Process;

namespace AutoRoute.Tests;

/// <summary>
/// Exit-path robustness: a late Avalonia DBus callback after teardown must not abort the process,
/// and a long-running child (pw-mon) must not outlive us when orderly disposal is skipped.
/// </summary>
public sealed class ShutdownTeardownTests
{
    // Exception.StackTrace is virtual, so the filter can be exercised with the exact frame shape seen
    // in the SIGABRT core dumps without standing up and tearing down a real Avalonia dispatcher.
    private sealed class TracedCancellation(string trace) : OperationCanceledException
    {
        public override string StackTrace => trace;
    }

    private const string DeadDispatcherTrace =
        "   at Avalonia.Threading.DispatcherOperation.Wait()\n" +
        "   at Avalonia.Threading.Dispatcher.InvokeImpl(DispatcherOperation operation, CancellationToken cancellationToken, TimeSpan timeout)\n" +
        "   at Avalonia.Threading.Dispatcher.Send(SendOrPostCallback action, Object arg, Nullable`1 priority)\n" +
        "   at Tmds.DBus.Protocol.DBusConnection.Observer.Emit(Exception exception)";

    [Fact]
    public void Dispatcher_cancellation_during_shutdown_is_benign()
    {
        Assert.True(ShutdownExceptionFilter.IsBenignTeardownException(
            new TracedCancellation(DeadDispatcherTrace), shuttingDown: true));
    }

    [Fact]
    public void Ui_thread_tray_cancellation_is_benign_only_once_stopping()
    {
        // Avalonia 12: disposing the tray cancels DBusTrayIconImpl.WatchAsync on the UI thread.
        var cancel = new TaskCanceledException();
        Assert.True(ShutdownExceptionFilter.IsBenignDispatcherException(cancel, shuttingDown: true));
        Assert.False(ShutdownExceptionFilter.IsBenignDispatcherException(cancel, shuttingDown: false));
        Assert.False(ShutdownExceptionFilter.IsBenignDispatcherException(
            new InvalidOperationException(), shuttingDown: true));
    }

    [Fact]
    public void Same_exception_before_shutdown_stays_fatal()
    {
        Assert.False(ShutdownExceptionFilter.IsBenignTeardownException(
            new TracedCancellation(DeadDispatcherTrace), shuttingDown: false));
    }

    [Fact]
    public void Other_exceptions_during_shutdown_stay_fatal()
    {
        // Not a cancellation, even with the dispatcher on the stack.
        var notCancel = new InvalidOperationException("boom");
        Assert.False(ShutdownExceptionFilter.IsBenignTeardownException(notCancel, shuttingDown: true));

        // A cancellation that never touched the dispatcher.
        var elsewhere = new TracedCancellation("   at AutoRoute.Engine.Reconciler.ApplyAsync()");
        Assert.False(ShutdownExceptionFilter.IsBenignTeardownException(elsewhere, shuttingDown: true));

        // No trace at all (never thrown).
        Assert.False(ShutdownExceptionFilter.IsBenignTeardownException(
            new OperationCanceledException(), shuttingDown: true));
    }

    [Fact]
    public async Task KillAllLive_stops_children_that_were_never_disposed()
    {
        var proc = new LongRunningProcess("sleep", new[] { "60" });
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += code => exited.TrySetResult(code);
        proc.Start();
        try
        {
            Assert.True(proc.IsRunning);
            Assert.True(LongRunningProcess.LiveCount >= 1);

            // What ProcessExit / UnhandledException run when DisposeAsync never gets the chance.
            LongRunningProcess.KillAllLive();

            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(proc.IsRunning);
        }
        finally
        {
            await proc.DisposeAsync();
        }
    }
}
