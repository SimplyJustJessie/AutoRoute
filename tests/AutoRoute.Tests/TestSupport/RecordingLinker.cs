using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire;

namespace AutoRoute.Tests.TestSupport;

/// <summary>
/// A fake <see cref="IPwLinker"/> that records every op instead of touching PipeWire, so the
/// reconciler's create/delete decisions can be asserted directly. Returns success by default.
/// </summary>
public sealed class RecordingLinker : IPwLinker
{
    public sealed record ConnectCall(int OutPortId, int InPortId, string RuleId);
    public sealed record DisconnectCall(int? LinkId, int? OutPortId, int? InPortId);

    public List<ConnectCall> Connects { get; } = new();
    public List<DisconnectCall> Disconnects { get; } = new();

    public int TotalOps => Connects.Count + Disconnects.Count;

    public Task<LinkOpResult> ConnectAsync(int outPortId, int inPortId, string ruleId, CancellationToken ct = default)
    {
        Connects.Add(new ConnectCall(outPortId, inPortId, ruleId));
        return Task.FromResult(LinkOpResult.Ok);
    }

    public Task<LinkOpResult> DisconnectAsync(int linkId, CancellationToken ct = default)
    {
        Disconnects.Add(new DisconnectCall(linkId, null, null));
        return Task.FromResult(LinkOpResult.Ok);
    }

    public Task<LinkOpResult> DisconnectAsync(int outPortId, int inPortId, CancellationToken ct = default)
    {
        Disconnects.Add(new DisconnectCall(null, outPortId, inPortId));
        return Task.FromResult(LinkOpResult.Ok);
    }
}
