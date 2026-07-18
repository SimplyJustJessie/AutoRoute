# Explicit pw-link (not pw-metadata), capable of fan-out

Supersedes [ADR-0001](0001-metadata-redirect-for-the-move.md).

**Context.** AutoRoute automates a setup the user already builds by hand in Helvum: a single stream fanned out to several sinks via explicit port links. Verified live — Spotify's `output_FL/FR` are linked to *both* the Ryzen analog sink (59) *and* MusicSink (87) at once. A stream node's `target.object` metadata is a single value and cannot express this fan-out, so the ADR-0001 redirect approach is a capability regression. The same snapshot shows WirePlumber does not keep re-asserting a default link once explicit links exist.

**Decision.** All routing is created as explicit `pw-link` connections addressed by numeric port ID; a Source's output ports may fan out to many Target Sinks. `pw-metadata` is not used to route.

**Consequences.** `pw-metadata` is dropped as a routing dependency. *Whether* the reconciler may delete links it does not own — and the positive/negative signing of rules that makes manual edits persist — is decided separately in [ADR-0007](0007-signed-rules-and-suppressions.md).
