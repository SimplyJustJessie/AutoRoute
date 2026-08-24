using AutoRoute.App.Services;
using AutoRoute.Engine;
using AutoRoute.PipeWire;
using Microsoft.Extensions.Logging;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// The Video tab's board — same coordinator and reconcile plumbing as <see cref="BoardViewModel"/>,
/// filtered to <c>Video/*</c> / <c>Stream/*/Video</c> nodes (a Spout2PW sender showing up as
/// <c>Stream/Output/Video</c>, OBS's PipeWire Video Capture source as <c>Stream/Input/Video</c>).
/// Shares the same singleton graph/rule-store/reconciler/linker/matcher as the Audio board — a rule
/// drawn here lands in the same <c>rules.json</c> and is reconciled by the same always-on
/// <c>RoutingWorker</c>, so a VTube Studio → OBS connection persists across relaunches exactly like
/// an audio one. No virtual-sink management (that's a PulseAudio-only concept).
/// </summary>
public sealed class VideoBoardViewModel : BoardViewModel
{
    public VideoBoardViewModel(
        IPwGraphService graph,
        IPwLinker linker,
        IRuleStore ruleStore,
        IReconciler reconciler,
        IRuleMatcher matcher,
        ILogger<BoardViewModel>? log = null,
        TabSelectionState? tabs = null)
        : base(graph, linker, ruleStore, reconciler, matcher,
            sinkController: null, notices: null, log: log, importer: null,
            autostart: null, update: null, kind: MediaKind.Video, tabs: tabs)
    {
    }
}
