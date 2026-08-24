namespace AutoRoute.App.Services;

/// <summary>
/// Which half of the PipeWire graph a board views. Nodes are classified by <c>media.class</c>
/// (<c>NodeRoles.IsOfKind</c>) — Audio covers <c>Audio/*</c> and <c>Stream/*/Audio</c> (app
/// streams, hardware/null sinks, captures); Video covers <c>Video/*</c> and <c>Stream/*/Video</c>
/// (a Spout2PW sender showing up as <c>Stream/Output/Video</c>, OBS's PipeWire video capture as
/// <c>Stream/Input/Video</c>). The reconciler and rule matcher never look at this — a Rule is just
/// stable-key criteria over live nodes, so a Video routing survives relaunches exactly like an
/// Audio one. This enum only gates which nodes a given board (tab) shows and drags.
/// </summary>
public enum MediaKind
{
    Audio,
    Video,
}
