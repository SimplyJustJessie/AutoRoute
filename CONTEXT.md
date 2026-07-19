# AutoRoute

Automated PipeWire routing manager — a *persistent Helvum*. It watches the audio graph and re-applies the user's routing decisions whenever matching nodes reappear, so a one-time drag-and-drop assignment survives the ephemeral node IDs that PipeWire regenerates each launch. Every manual edit persists in both directions: a Link the user makes is re-established, a Link the user breaks stays broken.

## Language

**Source**:
Any graph node with output ports — an app stream, a capture device, *or* a sink's monitor. Whatever the user can drag *from*. A sink is also a Source (via its monitor), which is what makes forwarding a sink's audio onward possible.
_Avoid_: "input" (ambiguous with a sink's input ports), "producer".

**Target Sink**:
Any graph node with input ports — whatever a Source can be dragged *onto*. Not restricted to hardware/null sinks; includes an app's *recording* stream (e.g. Discord's `recStream` / WEBRTC VoiceEngine), which is itself an ephemeral node.
_Avoid_: "destination", "output".

**Link**:
An explicit port-to-port connection made with `pw-link`, addressed by numeric port ID (never by name — names are ambiguous). The one and only routing mechanism. A single output port may Link to many Target Sinks (fan-out).
_Avoid_: "redirect", "route" (as a verb for making a connection).

**Managed Link**:
A Link a positive Rule created, identified by the `autoroute.managed` prop stamped on it at creation. The reconciler creates and maintains these. It deletes an *unowned* link only when a Suppression matches it (see below); every other unowned link it never touches.
_Avoid_: "our link", "auto link".

**Manual Link**:
Any link *not* created by a Rule — WirePlumber's default links, or ones the user makes by hand. Untagged. The reconciler neither reapplies nor cleans it up, *unless* a Suppression targets it, in which case it is deleted. The user may create or delete any Manual Link directly through the UI. A Manual Link made *outside* the app loads marked **unsaved** — shown but not reproduced after a relaunch until the user acts on it in-app (which turns it into a Rule).
_Avoid_: "unmanaged link" in UI copy (use "manual").

**Hop**:
A single edge in a routing chain, from one Source to one Target Sink (`game → GameSink` is one hop).

**Chain**:
The multi-hop path a signal takes (e.g. `mic → EasyEffects filters → Discord`). AutoRoute does *not* model chains as a unit — it creates independent single-hop Links. Hops owned by other apps (EasyEffects' internal filter graph, OBS's monitor capture) are made by those apps, not AutoRoute.

**Rule**:
A saved, *positive* assignment expressed as a **symmetric pair** of stable-key match-criteria — `sourceMatch → targetMatch` — resolved to live nodes every cycle; the reconciler Links every matching Source to every matching Target. Catches an ephemeral node on *either* side (an app's output stream *or* an app's recording target). Matches at **app granularity** — all of an app's streams count as one; per-tab isolation is out of scope (a browser's streams are indistinguishable by stable keys). Auto-persisted to `rules.json` the instant it is created or changed in the app — there is no explicit Save.

**Suppression**:
A saved, *negative* rule — the same `sourceMatch → targetMatch` pair, but meaning "keep these **un**linked." Created when the user manually disconnects a Link and it should stay gone. The reconciler enforces it by deleting any matching link on every cycle, **including unowned links** (this is the one case it deletes something it did not create). For any pair, the latest user action wins — connecting clears a Suppression, disconnecting overrides a positive Rule.

**Protected** ("do not touch"):
A stable-key match marking nodes AutoRoute must never route *or* unroute — it will neither create, delete, nor suppress any link touching a Protected node, overriding every Rule and Suppression. For routing owned by another tool (e.g. the `mic → EasyEffects → Discord` chain, which EasyEffects maintains itself). Expressed by stable keys so it survives the node's ephemeral reincarnations.
_Avoid_: "ignored", "excluded", "blacklist" (use "protected" / "do not touch").

**Reconcile**:
The idempotent core loop: from the rules and the current graph, create missing Managed Links, delete stale Managed Links, and delete any link matching an active Suppression (owned or not). It never touches any other unowned link. Since v2 the pass has two halves, sinks first: ensure every Declared Sink exists (and stale managed sink modules don't), then reconcile links.

**Declared Sink** (v2):
A virtual (null) sink listed in `rules.json` (`virtualSinks`) — the source of truth for which sinks AutoRoute owns. Declaring is what makes a sink **managed**: the reconciler keeps a matching `module-null-sink` loaded at runtime, and the generated pipewire-pulse drop-in recreates it at boot, so it exists even when AutoRoute isn't running. Identified by its `node.name`/`sink_name`; shown with a VIRTUAL chip and a delete affordance.
_Avoid_: "our sink" (say "managed" / "declared").

**Managed (virtual) Sink** (v2):
The live node/module realization of a Declared Sink. Carries an advisory `autoroute.managed=true` in its `sink_properties`; that tag only gates automatic cleanup of stale modules — ownership itself is name membership in the declared set.

**Adopted (unmanaged) sink** (v2):
A sink present in the graph but not declared — hardware, another app's null sink, or one from a legacy static conf the user hasn't retired. Rendered as a normal column: no chip, no delete affordance, and never auto-unloaded. Legacy conf files that still create sinks statically surface as a warning until the user removes them (their sinks are imported into the declared set at startup).

**Audio Format** (sample rate + bit depth):
The sample rate (Hz) and bit depth a node runs at, read from its `info.params` — the negotiated `Format` while a stream is active, else the default of the advertised `EnumFormat` for an idle device. Bit depth is derived from the PipeWire format token (`S16LE` → 16-bit, `F32LE`/`F32P` → 32-bit float, `S24_32LE` → 24-bit). Shown as a small badge ("48 kHz · 24-bit") on each palette Source and each Target Sink header. Presentation-only — it is never part of a node's stable match identity, so it never affects Rules, Suppressions, or Protected matching.
