# AutoRoute

**A persistent [Helvum](https://gitlab.freedesktop.org/pipewire/helvum) for PipeWire.** AutoRoute watches your audio graph and re-applies your routing decisions automatically — so a one-time drag-and-drop assignment survives the ephemeral node IDs PipeWire regenerates on every app launch.

Every manual edit persists **in both directions**: a link you make comes back when the nodes reappear, and a link you break stays broken — even with the window closed.

> `AutoRoute` is a working title — rename freely.

## Why

WirePlumber's stock policy links every app stream to the default sink. If you keep custom null sinks (`GameSink`, `MusicSink`, `DiscordSink`, …) to feed *separate* audio into OBS, Discord, and friends, the port links you make by hand in Helvum never persist — because PipeWire node IDs change every launch, you redo them every time an app (or Discord's per-call recording stream) reappears. AutoRoute makes those decisions stick.

## Features

- **Rules that survive relaunches** — matched by stable keys (application name, node name, …) at **app granularity**, not by ephemeral node ID.
- **Fan-out** — one source can feed many sinks at once (e.g. a game to both your headset *and* an OBS capture sink).
- **Signed edits** — a **positive rule** keeps a pair linked; a **suppression** keeps it *un*linked, deleting the link every time it reappears (including WirePlumber's default). The latest action wins.
- **Protected ("do not touch")** — mark nodes AutoRoute must never route or unroute, for routing owned by another tool (e.g. an `EasyEffects → Discord` chain). Precedence: **Protected > Suppression > Positive**.
- **Board UI** — a column per target sink, a palette of draggable sources; drag to connect, drop into many columns for fan-out. Not a patchbay.
- **Always-on** — a background watcher + tray icon reconcile the graph continuously; closing the window only hides it.
- **Safe ownership** — AutoRoute-created links are tagged `autoroute.managed`, so it only ever cleans up its own links (or ones you explicitly suppressed) and never touches your manual patches.

## Requirements

- Linux with **PipeWire** (`pw-dump`, `pw-link`, `pw-mon` on `PATH`) and WirePlumber
- **.NET 10 SDK** (10.0.1xx)

## Build & run

```bash
export PATH=/usr/share/dotnet:$PATH   # if the SDK isn't already on PATH
dotnet build
dotnet run --project src/AutoRoute.App                 # window
dotnet run --project src/AutoRoute.App -- --background  # tray only
```

Flags: `--background` (start hidden), `--poll` (use a polling graph monitor instead of `pw-mon`).

Rules are auto-persisted to `~/.config/autoroute/rules.json` on every in-app edit — there is no explicit Save.

### Autostart

A systemd user unit is provided in [`dist/systemd/autoroute.service`](dist/systemd/autoroute.service) (not installed by default). See [`dist/README.md`](dist/README.md) for the publish + `systemctl --user enable --now` steps.

## Project layout

```
src/AutoRoute.PipeWire/   # interop: graph model + pw-dump/pw-link/pw-mon drivers (no UI deps)
src/AutoRoute.Engine/     # rules, matching, and the idempotent reconciler + persistence
src/AutoRoute.App/        # Avalonia MVVM board UI + tray + always-on host
tests/AutoRoute.Tests/    # xUnit: parsing, matcher, reconciler, ownership round-trip, host
```

Stack: **C# / .NET 10**, **Avalonia** (MVVM), `Microsoft.Extensions.Hosting`.

## Design docs

The design is recorded in the repo:

- [`PLAN.md`](PLAN.md) — the v1 build spec
- [`CONTEXT.md`](CONTEXT.md) — the vocabulary / ubiquitous language
- [`docs/adr/`](docs/adr/) — architecture decision records (the *why* behind each choice)
- [`docs/dev/contracts.md`](docs/dev/contracts.md) — the internal module contracts

## Status

v1 is implemented and verified end-to-end against a live PipeWire graph: routing survives relaunches on fresh node IDs, suppressions stay enforced, fan-out and protected nodes behave, and the reconcile loop is idempotent.
