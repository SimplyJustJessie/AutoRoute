using System;
using System.Collections.Generic;
using System.Linq;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.PipeWire;

/// <summary>One resolved output-port → input-port pairing on a channel.</summary>
/// <param name="OutPortId">Source output port id.</param>
/// <param name="InPortId">Target input port id.</param>
/// <param name="Channel">The channel this pair carries (e.g. FL); "MONO" for a mono fan-out leg.</param>
public sealed record PortPair(int OutPortId, int InPortId, string Channel);

/// <summary>
/// Result of pairing one Source's outputs to one Target's inputs.
/// <see cref="UnmatchedSourceChannels"/> are surround channels the target can't accept
/// (surround guard → UI warning); <see cref="UnmatchedTargetChannels"/> are target inputs
/// nothing fed.
/// </summary>
public sealed record ChannelPairing(
    IReadOnlyList<PortPair> Pairs,
    IReadOnlyList<string> UnmatchedSourceChannels,
    IReadOnlyList<string> UnmatchedTargetChannels)
{
    public static readonly ChannelPairing Empty =
        new(Array.Empty<PortPair>(), Array.Empty<string>(), Array.Empty<string>());

    public bool HasWarnings => UnmatchedSourceChannels.Count > 0 || UnmatchedTargetChannels.Count > 0;
}

/// <summary>
/// Pure channel-pairing policy (PLAN "ChannelMapper"): match a Source's output ports to a
/// Target's input ports by <c>audio.channel</c> (FL→FL, FR→FR, …). A MONO source fans out to
/// the target's front-stereo (FL+FR) inputs — or to a mono target's single input. Unmatched
/// surround channels are left unlinked and surfaced for a UI warning. No side effects.
/// </summary>
public static class ChannelMapper
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] FrontStereo = { "FL", "FR" };

    public static ChannelPairing Map(PwNode source, PwNode target)
        => Map(source.OutputPorts.ToList(), target.InputPorts.ToList());

    public static ChannelPairing Map(IReadOnlyList<PwPort> sourceOutputs, IReadOnlyList<PwPort> targetInputs)
    {
        if (sourceOutputs.Count == 0 || targetInputs.Count == 0)
        {
            return new ChannelPairing(
                Array.Empty<PortPair>(),
                sourceOutputs.Select(ChannelLabel).ToList(),
                targetInputs.Select(ChannelLabel).ToList());
        }

        // MONO source: exactly one output with no/MONO channel → fan out to FL+FR (or mono target).
        if (sourceOutputs.Count == 1 && IsMono(sourceOutputs[0]))
            return FanOutMono(sourceOutputs[0], targetInputs);

        return MatchByChannel(sourceOutputs, targetInputs);
    }

    private static ChannelPairing FanOutMono(PwPort mono, IReadOnlyList<PwPort> targetInputs)
    {
        // Prefer the target's front L/R inputs; if it has none (e.g. a mono target), use all inputs.
        var fronts = targetInputs.Where(p => p.Channel is not null && FrontStereo.Contains(p.Channel, Cmp)).ToList();
        var chosen = fronts.Count > 0 ? fronts : targetInputs.ToList();

        var pairs = chosen
            .Select(t => new PortPair(mono.Id, t.Id, ChannelLabel(t)))
            .ToList();

        var unmatchedTarget = targetInputs
            .Where(t => chosen.All(c => c.Id != t.Id))
            .Select(ChannelLabel)
            .ToList();

        return new ChannelPairing(pairs, Array.Empty<string>(), unmatchedTarget);
    }

    private static ChannelPairing MatchByChannel(IReadOnlyList<PwPort> sourceOutputs, IReadOnlyList<PwPort> targetInputs)
    {
        // First input port per channel wins (channels are normally unique within a node).
        var targetByChannel = new Dictionary<string, PwPort>(Cmp);
        foreach (var t in targetInputs)
        {
            if (t.Channel is null) continue;
            targetByChannel.TryAdd(t.Channel, t);
        }

        var pairs = new List<PortPair>();
        var unmatchedSource = new List<string>();
        var matchedTargetChannels = new HashSet<string>(Cmp);

        foreach (var s in sourceOutputs)
        {
            if (s.Channel is not null && targetByChannel.TryGetValue(s.Channel, out var t))
            {
                pairs.Add(new PortPair(s.Id, t.Id, s.Channel));
                matchedTargetChannels.Add(s.Channel);
            }
            else
            {
                unmatchedSource.Add(ChannelLabel(s));
            }
        }

        var unmatchedTarget = targetByChannel.Keys
            .Where(ch => !matchedTargetChannels.Contains(ch))
            .ToList();

        return new ChannelPairing(pairs, unmatchedSource, unmatchedTarget);
    }

    private static bool IsMono(PwPort p)
        => p.Channel is null || Cmp.Equals(p.Channel, "MONO");

    private static string ChannelLabel(PwPort p) => p.Channel ?? "MONO";
}
