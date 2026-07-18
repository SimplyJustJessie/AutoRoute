# App-managed virtual sinks

> **Status: accepted — v2.** The mechanism fork below is settled: **hybrid**, with `pactl` as the
> runtime tool. Build spec: [PLAN.v2.md](../../PLAN.v2.md). Gate script: `scripts/v2-gate.sh` (M1).

**Context.** Historically the null sinks (GameSink, MusicSink, DiscordSink, DesktopSink) were declared statically in `~/.config/pipewire/pipewire-pulse.conf.d/virtual-sinks.conf` and created at launch by that config. The user wants to create and remove virtual sinks from a button inside AutoRoute, retiring that static config — so AutoRoute owns the **virtual-sink lifecycle**, not only the links between existing nodes.

**Decision.** AutoRoute gains UI to create and delete virtual (null) sinks. The creation-mechanism fork is settled as **hybrid**:

- Declared sinks persist in `rules.json` (schema v2, `virtualSinks` array) — the single source of truth, so "delete sink + delete its rules" is one atomic save.
- AutoRoute generates `~/.config/pipewire/pipewire-pulse.conf.d/autoroute-sinks.conf` (write-if-changed, atomic, fully owned/overwritten) so sinks are recreated at boot by pipewire-pulse itself — **sink existence stays independent of AutoRoute running**, matching the old static behaviour.
- Instant effect comes from `pactl load-module module-null-sink` / `pactl unload-module` at runtime; the always-on `SinkReconciler` pass converges live state to the declared set on every graph/rules change.

**Why `pactl`, not `pw-cli`:** a one-shot `pw-cli create-node` binds the created object to the pw-cli client connection — the node dies when the process exits (gate step 5 re-verifies this live). `pactl` modules load into pipewire-pulse, so they outlive AutoRoute and die exactly when the drop-in recreates them; both the drop-in's `pulse.cmd` entries and runtime loads produce the *same* module kind, visible and unloadable via `pactl list modules` (load-path symmetry, gate step 4).

**Ownership:** a sink is AutoRoute-managed iff its `node.name`/`sink_name` is in the declared set. `autoroute.managed=true` is additionally stamped into `sink_properties` on both paths — used for diagnostics and as the safety guard for stale-module auto-cleanup (only *tagged* modules whose name is no longer declared are auto-unloaded; untagged modules are never touched). If the gate shows the custom prop stripped, only that auto-cleanup is dropped; ownership was never prop-based.

**Double-creation guard:** before loading a declared-but-absent sink, the reconciler checks `pactl list modules short` for an existing `module-null-sink` with that `sink_name` — covering the boot race (drop-in loads while AutoRoute starts) and the load→snapshot window. Gate step 6 documents that a duplicate `load-module` would otherwise create a second same-named sink.

**Migration:** at startup AutoRoute parses the legacy conf files (tolerantly, skip-on-failure) and imports their sinks into the declared set, but only **warns** about files still creating sinks statically — removing them stays the user's action, so AutoRoute never edits config it doesn't own.

**Deleting a sink** prompts with the Rules/Suppressions referencing it and offers to delete them in the same save (default on); declining leaves them dormant — they re-match if a same-named sink ever returns.

**Consequences.** Removes the manual config/script and makes sink management a first-class in-app action. Virtual sinks remain AutoRoute-independent at boot via the generated drop-in; runtime create/delete is instant via pactl; the two paths cannot double-create.

## Gate evidence (M1 — run live with `--restart`, 2026-07-18: **11 pass, 0 fail**)

`scripts/v2-gate.sh --restart` against the real PipeWire session:

- **Step 1 PASS** — `pactl load-module` returned module index 536870920; the node appeared in `pw-dump` as `media.class=Audio/Sink` **with `autoroute.managed` round-tripping (as a JSON boolean `true`**, same normalization as the v1 link tag — ownership is name-based anyway, so nothing depends on it beyond stale-module cleanup, which stands). Live capture committed as `tests/AutoRoute.Tests/fixtures/pw-dump.managed-sink.json`.
- **Step 2 PASS** — `pactl list modules short` row carries `sink_name=` and the ownership tag in its args. Live capture committed as `tests/AutoRoute.Tests/fixtures/pactl-modules.short.sample.txt` (replacing the hand-authored sample). It also revealed the **real legacy arg shape** — `sink_properties=device.description='Music Sink'`, *no* outer double quotes — which exposed and fixed an importer bug that would have truncated space-containing descriptions to their first word.
- **Step 3 PASS** — `unload-module` removed the node.
- **Step 4 PASS** — the generated drop-in created the sink at pipewire-pulse start; the **boot-loaded module is visible to pactl** (load-path symmetry, the property the hybrid design rests on); removal + restart came back clean.
- **Step 5 PASS** — one-shot `pw-cli create-node` node vanished on process exit, confirming why pactl was chosen.
- **Step 6 INFO** — a duplicate `load-module` with the same `sink_name` **did create a second module** (536870920, 536870921) — the reconciler's modules-list guard before every load is confirmed REQUIRED, and is implemented.

The gate is fully green: every load-bearing assumption of the hybrid mechanism is verified against the real system, and the committed fixtures are the live captures.
