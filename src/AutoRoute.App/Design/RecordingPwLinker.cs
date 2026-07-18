using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire;

namespace AutoRoute.App.Design;

/// <summary>
/// Recording no-op <see cref="IPwLinker"/> for standalone UI development: records every connect /
/// disconnect op (so tests/dev can assert the board issued them) and always reports success. Wave 3
/// replaces this with the real <c>PwLinker</c> shelling out to <c>pw-link</c>.
/// </summary>
public sealed class RecordingPwLinker : IPwLinker
{
    public readonly record struct ConnectOp(int OutPortId, int InPortId, string RuleId);
    public readonly record struct DisconnectOp(int? LinkId, int? OutPortId, int? InPortId);

    public List<ConnectOp> Connects { get; } = new();
    public List<DisconnectOp> Disconnects { get; } = new();

    public Task<LinkOpResult> ConnectAsync(int outPortId, int inPortId, string ruleId, CancellationToken ct = default)
    {
        Connects.Add(new ConnectOp(outPortId, inPortId, ruleId));
        return Task.FromResult(LinkOpResult.Ok);
    }

    public Task<LinkOpResult> DisconnectAsync(int linkId, CancellationToken ct = default)
    {
        Disconnects.Add(new DisconnectOp(linkId, null, null));
        return Task.FromResult(LinkOpResult.Ok);
    }

    public Task<LinkOpResult> DisconnectAsync(int outPortId, int inPortId, CancellationToken ct = default)
    {
        Disconnects.Add(new DisconnectOp(null, outPortId, inPortId));
        return Task.FromResult(LinkOpResult.Ok);
    }
}
