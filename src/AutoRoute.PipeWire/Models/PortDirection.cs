namespace AutoRoute.PipeWire.Models;

/// <summary>
/// Direction of a PipeWire port relative to its owning node.
/// A node's <see cref="PortDirection.Output"/> ports feed other nodes (a Source);
/// its <see cref="PortDirection.Input"/> ports receive audio (a Target Sink).
/// pw-dump reports this as <c>info.direction = "input" | "output"</c>.
/// </summary>
public enum PortDirection
{
    Input,
    Output,
}
