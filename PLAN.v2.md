# AutoRoute v2 — App-Managed Virtual Sinks

> **Design source of truth:** [CONTEXT.md](CONTEXT.md) (vocabulary) + [ADR-0011](docs/adr/0011-app-managed-virtual-sinks.md) (rationale, **accepted**). This file is the **v2 build spec**, structured like [PLAN.md](PLAN.md) (the immutable v1 spec). Frozen surfaces: [docs/dev/contracts.md](docs/dev/contracts.md) §6.

## Problem

v1 made *links* persistent, but the null sinks they route into (GameSink, MusicSink, DiscordSink, DesktopSink) were still declared by hand in a static `~/.config/pipewire/pipewire-pulse.conf.d/virtual-sinks.conf`. v2 makes AutoRoute own the **virtual-sink lifecycle**: create and delete null sinks from the board, keep them alive across PipeWire restarts, recreate them at boot without AutoRoute running, and retire the static file.

## Locked decisions

| Area | Decision |
|---|---|
| Mechanism | **Hybrid** ([0011](docs/adr/0011-app-managed-virtual-sinks.md)): declared set in `rules.json` (v2, `virtualSinks`) → generated conf.d drop-in for boot persistence **and** instant `pactl` load/unload at runtime |
| Runtime tool | **`pactl`** — pw-cli one-shot nodes die with the process (gate step 5); pactl modules live in pipewire-pulse, symmetric with the drop-in's `pulse.cmd` entries |
| Ownership | **Name membership in the declared set**; `autoroute.managed=true` in `sink_properties` is advisory + gates stale-module auto-unload only |
| Persistence | `rules.json` **version 2** (not a separate sinks.json) — sink-delete + rule-delete must be one atomic save; reuses RuleStore atomic write/hot-reload wholesale |
| Drop-in | `$XDG_CONFIG_HOME/pipewire/pipewire-pulse.conf.d/autoroute-sinks.conf`, fully generated (header marks it), write-if-changed, atomic, deleted when no sinks declared |
| Reconcile | New `SinkReconciler.EnsureAsync` runs **before** link reconcile in `RoutingWorker`; per-name backoff (5s→30s→2min); `pactl list modules short` check prevents double-creation |
| Migration | **Detect + import, manual retire**: startup import of legacy conf files (tolerant parser), warn-only banner + journal while they remain |
| Sink deletion | **Prompt**: confirm flyout lists Rules/Suppressions referencing the sink, "also delete these" checkbox default-on, all in one save |
| UI | "+ New Sink" toolbar flyout (name/description/stereo-mono); VIRTUAL chip + delete button on managed columns; amber legacy banner |

## Build sequence (M1–M7 — all shipped except the live gate run)

1. **Gate (`scripts/v2-gate.sh` — run on the real machine):** verify pactl mechanics live: module index + `autoroute.managed` node-prop round-trip, module-list shape, unload, drop-in boot + pactl visibility (`--restart`), pw-cli negative, duplicate-name behaviour. Captures real fixtures. **If the custom prop is stripped: drop stale-module auto-cleanup only.**
2. **Model/persistence:** `VirtualSinkSpec`, `RulesDocument` v2 + `Normalized()`, `SinkNameValidator`.
3. **Driver:** `IVirtualSinkController` / `PactlSinkController` (PwLinker's fail-don't-throw posture; `BuildModuleArgs` shared with the drop-in generator).
4. **Reconcile:** `SinkDropInWriter` (+ `AtomicFile` extracted from RuleStore), `SinkReconciler`, `RoutingWorker` sinks-before-links, DI registration.
5. **Import:** `PulseConfImporter` one-shot at startup → `AppNotices` banner + journal warnings.
6. **UI:** `IBoardCoordinator.CreateSinkAsync/PreviewDeleteSink/DeleteSinkAsync`, create/delete flyouts, VIRTUAL chip, banner, `MockSinkController` in the harnesses.
7. **Docs:** ADR-0011 accepted, this file, contracts §6, CONTEXT vocabulary.

## Verification (end-to-end, on the real machine)

0. `./scripts/v2-gate.sh --restart` → all steps PASS; commit the refreshed fixtures.
1. **Create:** "+ New Sink" → `TestSink`. Within ~1 s the column appears with the VIRTUAL chip;
   `pw-dump | jq '.[]|select(.info.props["node.name"]=="TestSink")'` shows `Audio/Sink` with `autoroute.managed`;
   `pactl list modules short | grep sink_name=TestSink` → exactly one row;
   `~/.config/pipewire/pipewire-pulse.conf.d/autoroute-sinks.conf` contains the entry.
2. **Restart resilience:** `systemctl --user restart pipewire pipewire-pulse wireplumber` with AutoRoute running → TestSink returns; `pactl list modules short | grep -c sink_name=TestSink` = **1** (no double-creation from the drop-in + watcher racing).
3. **AutoRoute-independence:** tray-Quit AutoRoute; `systemctl --user restart pipewire-pulse` → TestSink still exists (drop-in alone).
4. **Migration:** with the legacy `virtual-sinks.conf` present, start AutoRoute → its sinks land in `rules.json` `virtualSinks`, amber banner names the file, journal warns; delete the file → banner gone on next start, sinks persist via our drop-in.
5. **v1 flagship on a v2 sink:** drag a source onto TestSink, relaunch the app → link reappears.
6. **Delete:** delete TestSink while a rule targets it → confirm flyout lists the rule; confirm with the checkbox on → sink gone from graph, `pactl list modules`, and the drop-in; rule gone from `rules.json` — one save (`jq . ~/.config/autoroute/rules.json`).

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| `sink_properties` custom key stripped | Gate step 1; ownership is already name-based — only stale auto-cleanup is dropped |
| Duplicate sinks (drop-in boot race / double load) | `pactl list modules short` check before every load; gate step 6 documents duplicate behaviour |
| Generated drop-in breaks pipewire-pulse startup | Strict name regex + no-quotes descriptions (`SinkNameValidator`), golden `Generate` tests, gate step 4 boots the real format |
| pactl absent / pipewire-pulse down | Load failures return `Fail` → per-name backoff (no load-loop); UI hidden when no controller is wired |
| Unloading a user-owned module | Auto-cleanup requires the `autoroute.managed` tag in module args; UI delete is name-scoped and user-confirmed |
| Save→Changed→ensure→write feedback loop | Drop-in writes never touch rules.json; conf.d isn't watched; RuleStore's self-write suppression covers the rules side |
| Exotic legacy conf misparses | Per-entry skip-on-failure, logged, never blocks startup; the user's file shape is the committed fixture |
| rules.json v2 vs hand-edits / old binaries | `Normalized()` accepts v1 silently; malformed JSON keeps last-good (unchanged v1 posture) |
