using System.Threading;
using System.Threading.Tasks;

namespace AutoRoute.PipeWire;

/// <summary>Outcome of a single link create/destroy operation.</summary>
/// <param name="Success">True if pw-link reported success.</param>
/// <param name="Error">Failure detail when <see cref="Success"/> is false (e.g. port vanished).</param>
public readonly record struct LinkOpResult(bool Success, string? Error)
{
    public static LinkOpResult Ok => new(true, null);
    public static LinkOpResult Fail(string error) => new(false, error);
}

/// <summary>
/// Creates and destroys Links via <c>pw-link</c>, always by numeric port/link id (ADR-0002).
/// Every created link is stamped with the <c>autoroute.managed</c> / <c>autoroute.rule</c>
/// ownership tag (ADR-0004). Operations never throw for the expected transient failure
/// (a port vanished mid-cycle): they log and return <see cref="LinkOpResult.Fail"/> so the
/// reconciler self-heals on the next snapshot.
/// </summary>
public interface IPwLinker
{
    /// <summary>Create a managed link from output port to input port, tagged with <paramref name="ruleId"/>.</summary>
    Task<LinkOpResult> ConnectAsync(int outPortId, int inPortId, string ruleId, CancellationToken ct = default);

    /// <summary>Destroy a link by its numeric link id.</summary>
    Task<LinkOpResult> DisconnectAsync(int linkId, CancellationToken ct = default);

    /// <summary>Destroy the link between a specific output and input port pair.</summary>
    Task<LinkOpResult> DisconnectAsync(int outPortId, int inPortId, CancellationToken ct = default);
}
