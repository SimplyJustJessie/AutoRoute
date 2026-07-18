namespace AutoRoute.PipeWire.Models;

/// <summary>
/// A single PipeWire port, addressed by its numeric <see cref="Id"/> — the only stable
/// handle we ever pass to <c>pw-link</c> (names are ambiguous, see ADR-0002 / CONTEXT.md).
/// </summary>
/// <param name="Id">Numeric object id of the Port (pw-dump top-level <c>.id</c>).</param>
/// <param name="NodeId">Numeric id of the owning node (<c>info.props["node.id"]</c>).</param>
/// <param name="Direction">Input or Output (<c>info.direction</c>).</param>
/// <param name="PortName">Human-readable port name, e.g. <c>output_FL</c> (<c>info.props["port.name"]</c>). May be null.</param>
/// <param name="Channel">Audio channel, e.g. <c>FL</c>/<c>FR</c>/<c>MONO</c> (<c>info.props["audio.channel"]</c>). Null for MIDI/DSP/control ports.</param>
/// <param name="PortIndex">Port index within its node (<c>info.props["port.id"]</c>).</param>
public sealed record PwPort(
    int Id,
    int NodeId,
    PortDirection Direction,
    string? PortName,
    string? Channel,
    int PortIndex)
{
    public bool IsOutput => Direction == PortDirection.Output;
    public bool IsInput => Direction == PortDirection.Input;
}
