# Use metadata-redirect (not link-deletion) to move a stream

> **Status: Superseded by [ADR-0002](0002-pure-pw-link-fan-out.md).** A `target.object` redirect holds a single value and cannot express stream-level fan-out (one stream → several sinks), which the live graph proved is a core, already-in-use requirement. The WirePlumber "fight" this ADR guarded against also does not occur in practice. Kept for the record.

**Context.** An exclusive Rule must move an app stream (e.g. Zen) off the default headset onto a null sink (GameSink). WirePlumber's stock policy auto-links every stream to the default sink and will keep re-creating that link, so a `pw-link -d` "disconnect war" both (a) fights WirePlumber in a potential flap loop and (b) requires deleting a link AutoRoute doesn't own — violating our "never touch unowned links" safety rule.

**Decision.** For hop 1 (the move), set the stream node's routing metadata via `pw-metadata` (`target.object`) so WirePlumber itself routes the stream into the target sink. For later hops that nothing else competes to create (e.g. a sink's monitor → hardware sink), keep explicit `pw-link` by numeric port ID. Thus `AutoRoute.PipeWire` drives **both** `pw-metadata` and `pw-link`.

**Consequences.** No competing link is ever created, so there is nothing to disconnect and no flap loop; the "never delete unowned links" guarantee stays intact. Cost: two distinct routing mechanisms (Redirect vs Link) with different ownership and reconcile semantics, and a dependency on `target.object` metadata behaving as expected (verify empirically).
