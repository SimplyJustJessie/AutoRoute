using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoRoute.App.Hosting;

/// <summary>
/// Single-instance guard over a unix domain socket (ADR-0005). The first process to
/// <see cref="TryAcquireAsync"/> binds <c>$XDG_RUNTIME_DIR/autoroute.sock</c> and owns the sole
/// host / worker / tray. A later launch calls <see cref="SignalRevealAsync"/> to reach the primary
/// (which raises its <c>onReveal</c> callback) and then exits without building a host.
///
/// <para>Stale-socket handling: a socket file left by an unclean exit refuses connections, so the
/// probe in <see cref="TryAcquireAsync"/> fails, and the guard unlinks the dead file before binding
/// a fresh one — exactly as the ADR prescribes.</para>
///
/// <para>The class is deliberately free of any host/UI dependency so the handshake can be unit-tested
/// in isolation (see <c>SingleInstanceGuardTests</c>).</para>
/// </summary>
public sealed class SingleInstanceGuard : IAsyncDisposable
{
    /// <summary>The reveal message a secondary sends to the primary over the socket.</summary>
    public const string RevealMessage = "reveal";

    private readonly CancellationTokenSource _cts = new();
    private Socket? _listener;
    private bool _bound;

    public string SocketPath { get; }

    /// <param name="socketPath">
    /// Override the socket location (tests). Defaults to <c>$XDG_RUNTIME_DIR/autoroute.sock</c>,
    /// falling back to the temp dir when <c>XDG_RUNTIME_DIR</c> is unset.
    /// </param>
    public SingleInstanceGuard(string? socketPath = null)
    {
        SocketPath = socketPath ?? DefaultSocketPath();
    }

    /// <summary><c>$XDG_RUNTIME_DIR/autoroute.sock</c> (temp dir when the env var is unset).</summary>
    public static string DefaultSocketPath()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir))
            runtimeDir = Path.GetTempPath();
        return Path.Combine(runtimeDir, "autoroute.sock");
    }

    /// <summary>
    /// Try to become the primary instance. Returns <c>true</c> and starts listening (invoking
    /// <paramref name="onReveal"/> for each reveal message received) when this process bound the
    /// socket; returns <c>false</c> when a live primary already holds it — the caller should then
    /// <see cref="SignalRevealAsync"/> and exit.
    /// </summary>
    public async Task<bool> TryAcquireAsync(Action onReveal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onReveal);

        // A live primary answers a probe connect → we are a secondary.
        if (await CanConnectAsync(SocketPath, ct).ConfigureAwait(false))
            return false;

        // No one answered. Any file present is stale (unclean exit) — unlink before binding.
        TryUnlink(SocketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            listener.Listen(16);
        }
        catch (SocketException)
        {
            listener.Dispose();
            // Lost a start-up race: another process bound between our probe and our bind. If it is
            // now answering, concede and behave as a secondary; otherwise the bind genuinely failed.
            if (await CanConnectAsync(SocketPath, ct).ConfigureAwait(false))
                return false;
            throw;
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        _listener = listener;
        _bound = true;
        _ = Task.Run(() => AcceptLoopAsync(onReveal, _cts.Token));
        return true;
    }

    /// <summary>
    /// Connect to the primary and send a reveal message. Returns <c>true</c> when delivered,
    /// <c>false</c> when no primary was reachable.
    /// </summary>
    public async Task<bool> SignalRevealAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes(RevealMessage + "\n");
            await client.SendAsync(payload, SocketFlags.None, ct).ConfigureAwait(false);
            // Give the primary a beat to read before we drop the connection.
            client.Shutdown(SocketShutdown.Both);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(Action onReveal, CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = HandleConnectionAsync(connection, onReveal, ct);
        }
    }

    private static async Task HandleConnectionAsync(Socket connection, Action onReveal, CancellationToken ct)
    {
        try
        {
            using (connection)
            {
                var buffer = new byte[64];
                var received = await connection.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                if (received <= 0) return;
                var text = Encoding.UTF8.GetString(buffer, 0, received).Trim();
                if (text.StartsWith(RevealMessage, StringComparison.Ordinal))
                    onReveal();
            }
        }
        catch
        {
            // A malformed or dropped connection never affects the primary.
        }
    }

    private static async Task<bool> CanConnectAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryUnlink(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort; a failed unlink surfaces as a bind failure below */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch { /* ignore */ }

        _listener?.Dispose();
        _listener = null;

        if (_bound)
            TryUnlink(SocketPath);

        _cts.Dispose();
    }
}
