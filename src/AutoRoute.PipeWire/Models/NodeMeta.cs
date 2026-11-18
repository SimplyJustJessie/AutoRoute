namespace AutoRoute.PipeWire.Models;

/// <summary>
/// The parts of a node's <c>info</c> that only presentation and volume control need, kept out of
/// <see cref="PwNode"/>'s identity fields so matching and diffing never depend on them.
/// </summary>
/// <param name="Nick"><c>node.nick</c> — ALSA's short device label (e.g. <c>HDMI 0</c>).</param>
/// <param name="DeviceBus"><c>device.bus</c>: <c>usb</c>, <c>pci</c>, <c>bluetooth</c>… Null for virtual nodes and app streams.</param>
/// <param name="DeviceId"><c>device.id</c> — groups a device's sink and source nodes (headset detection).</param>
/// <param name="IsVirtual"><c>node.virtual</c>.</param>
/// <param name="ApplicationId"><c>application.id</c> (often the desktop-file id, e.g. <c>com.github.wwmm.easyeffects</c>).</param>
/// <param name="State"><c>info.state</c>: <c>running</c>, <c>idle</c>, <c>suspended</c>…</param>
/// <param name="Volume">Volume on the user-facing cubic scale (what <c>wpctl</c> and pavucontrol show, 1.0 = 100%): the cube root of the loudest <c>channelVolumes</c> entry. Null when the node has no volume.</param>
/// <param name="Muted"><c>Props.mute</c>, null when not reported.</param>
/// <param name="IconName"><c>application.icon_name</c>, when the app names its own icon.</param>
/// <param name="FlatpakId"><c>pipewire.access.portal.app_id</c>: the Flatpak app id of a sandboxed app.</param>
/// <param name="MonitorHasVolume"><c>monitor.channel-volumes</c>: the sink's monitor carries its volume (AutoRoute's own virtual sinks set it).</param>
/// <param name="Serial"><c>object.serial</c>: never reused, and what pipewire-pulse calls the object's index.</param>
public sealed record NodeMeta(
    string? Nick = null,
    string? DeviceBus = null,
    int? DeviceId = null,
    bool IsVirtual = false,
    string? ApplicationId = null,
    string? State = null,
    double? Volume = null,
    bool? Muted = null,
    uint? Serial = null,
    bool MonitorHasVolume = false,
    string? IconName = null,
    string? FlatpakId = null)
{
    public static readonly NodeMeta Empty = new();

    /// <summary>Audio is flowing through the node right now.</summary>
    public bool IsRunning => State == "running";
}
