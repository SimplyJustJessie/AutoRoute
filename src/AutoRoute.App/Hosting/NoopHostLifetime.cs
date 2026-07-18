using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace AutoRoute.App.Hosting;

/// <summary>
/// A do-nothing <see cref="IHostLifetime"/> that replaces the Generic Host's default
/// <c>ConsoleLifetime</c>. ConsoleLifetime installs its own SIGTERM/SIGINT handlers (and prints
/// "Press Ctrl+C to shut down"); left in place it competes with the app's Avalonia-integrated signal
/// handler and neither shutdown completes — the host is only a DI container + BackgroundService
/// runner here, while the Avalonia classic-desktop lifetime + the app's own
/// <see cref="System.Runtime.InteropServices.PosixSignalRegistration"/> own process lifetime.
/// </summary>
public sealed class NoopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
