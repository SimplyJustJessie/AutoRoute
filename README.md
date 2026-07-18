# AutoRoute

[![CI](https://github.com/SimplyJustJessie/AutoRoute/actions/workflows/ci.yml/badge.svg)](https://github.com/SimplyJustJessie/AutoRoute/actions/workflows/ci.yml)

**Wire your PipeWire audio once, and keep it wired.**

AutoRoute is a background service and GUI for Linux that remembers your PipeWire connections and re-applies them as apps come and go. It's a *persistent* [Helvum](https://gitlab.freedesktop.org/pipewire/helvum): you patch things the way you want, and AutoRoute keeps them that way — even though PipeWire hands out fresh node IDs every time an app restarts.

## The problem

By default WirePlumber routes every app to your default output. The moment you run separate virtual sinks — a `GameSink` feeding OBS, a `MusicSink`, a `DiscordSink` — you're back in Helvum re-patching by hand, because node IDs change on every launch and streams like Discord's per-call recording input reappear constantly.

AutoRoute makes those patches stick, in **both directions**:

- A connection you make is **re-created** whenever the matching apps come back.
- A connection you remove **stays** removed — even when WirePlumber tries to re-add its default link.

## Features

- **Rules that survive relaunches** — connections are matched by application, not by throwaway node IDs.
- **Fan-out** — send one source to several sinks at once (your headset *and* a capture sink for streaming).
- **Keep-connected and keep-disconnected** — positive rules hold a link open; suppressions keep it closed on every cycle.
- **Protected nodes** — mark routing that another tool owns (e.g. an EasyEffects chain) as off-limits, and AutoRoute won't touch it. Precedence is absolute: *protected > suppression > connect*.
- **A board, not a patchbay** — one column per sink, a palette of draggable sources; drag to connect, drop into several columns to fan out. Channels are paired for you.
- **Runs in the background** — a tray app (and an optional systemd service) reconcile the graph continuously; closing the window just hides it.
- **Non-destructive** — AutoRoute only ever removes links it created or ones you explicitly suppressed. Your other manual patches are never touched.

## Requirements

- Linux with **PipeWire** and **WirePlumber** (`pw-dump`, `pw-link`, `pw-mon` available on your `PATH`)
- The [**.NET 10 SDK**](https://dotnet.microsoft.com/download)

## Install

```bash
git clone https://github.com/SimplyJustJessie/AutoRoute.git
cd AutoRoute
dotnet build
```

## Run

```bash
dotnet run --project src/AutoRoute.App              # open the window
dotnet run --project src/AutoRoute.App -- --background   # tray only, no window
```

Flags:

- `--background` — start hidden (tray only)
- `--poll` — poll `pw-dump` for changes instead of watching `pw-mon`

## Using it

1. Launch AutoRoute — the board mirrors your current audio graph. Links that already exist show up as **unsaved**.
2. Drag a source (an app stream, a microphone, or a sink's monitor) from the palette into a target sink's column to connect it. Drop it into more than one column to fan out.
3. Remove a card to disconnect. Removing an external link records a **suppression** so it stays gone.
4. Mark a node **protected** to tell AutoRoute to leave it and everything touching it alone.

Rules are written to `~/.config/autoroute/rules.json` the moment you make a change — there is no Save button.

## Autostart

A systemd user service is included so AutoRoute starts with your session. After building/publishing (see [`dist/README.md`](dist/README.md)):

```bash
systemctl --user enable --now autoroute.service
journalctl --user -u autoroute -f     # follow its logs
```

The unit lives at [`dist/systemd/autoroute.service`](dist/systemd/autoroute.service).

## How it works

AutoRoute reads the graph from `pw-dump`, matches it against your rules, and makes only the `pw-link` changes needed to reach the state you asked for — an idempotent reconcile that runs whenever the graph or your rules change. Every link it creates is tagged `autoroute.managed`, so it can always tell its own links from yours and never cleans up a connection it didn't make. `pw-mon` tells it *when* to re-check; it never routes by name, only by numeric port ID.

```
src/AutoRoute.PipeWire/   # PipeWire interop: graph model + pw-dump/pw-link/pw-mon drivers
src/AutoRoute.Engine/     # rule matching, the reconciler, and rules.json persistence
src/AutoRoute.App/        # Avalonia MVVM board UI, tray, and the always-on host
tests/AutoRoute.Tests/    # xUnit test suite
```

Built with C# / .NET 10 and [Avalonia](https://avaloniaui.net/).

## Design notes

The reasoning behind the architecture is written down in the repo:

- [`PLAN.md`](PLAN.md) — the build spec
- [`CONTEXT.md`](CONTEXT.md) — the project's vocabulary
- [`docs/adr/`](docs/adr/) — architecture decision records
