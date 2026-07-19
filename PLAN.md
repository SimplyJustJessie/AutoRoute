# AutoRoute — Automated PipeWire Routing Manager

> **Design source of truth:** [CONTEXT.md](CONTEXT.md) (vocabulary) + [docs/adr/](docs/adr/) (rationale). This file is the **v1 build spec**; where it and an ADR disagree, the ADR wins. The original pre-grilling plan is archived at [PLAN.original.md](PLAN.original.md).
>
> `AutoRoute` is a placeholder name — rename freely. Root namespace `AutoRoute`, config dir `~/.config/autoroute/`, project root `/home/jessie/Files/Dev/AutoRoute/`.

## Problem

WirePlumber's stock policy links every app stream to the default sink (PRO X 2 headset, id 55). You keep custom null sinks — `GameSink`, `MusicSink`, `DiscordSink`, `DesktopSink` — to feed *separate* audio into OBS, Discord, etc. Because PipeWire node IDs are regenerated every launch, the port links you make by hand in **Helvum** never persist: you redo them every time an app — or Discord's per-call recording stream — reappears.

AutoRoute is a **persistent Helvum**: it watches the graph and re-applies your routing decisions whenever matching nodes reappear, in *both* directions — a Link you make comes back, a Link you break stays broken — even with the window closed.

## Locked decisions (v1 = all of it)

| Area | Decision | ADR |
|---|---|---|
| Stack | C# + Avalonia UI on .NET 10 (SDK 10.0.110), MVVM | — |
| Automation | Single-process GUI + tray + background watcher; reconciles with the window closed | — |
| Mechanism | Explicit `pw-link` by **numeric port ID**, **fan-out** capable; **no** `pw-metadata` routing | [0002](docs/adr/0002-pure-pw-link-fan-out.md) |
| Matching | Symmetric `sourceMatch → targetMatch`, stable keys, **app granularity** | [0003](docs/adr/0003-app-granularity-matching.md), [0006](docs/adr/0006-symmetric-match-rules.md) |
| Rule signing | Positive (connect) + **Suppression** (disconnect; persists; deletes even WirePlumber's link) | [0007](docs/adr/0007-signed-rules-and-suppressions.md) |
| Protection | **Protected** "do not touch" overrides all. Precedence: **Protected > Suppression > Positive** | [0008](docs/adr/0008-protected-do-not-touch.md) |
| Ownership | `autoroute.managed` link **prop tag**; no ledger (gated on a build-time round-trip test) | [0004](docs/adr/0004-ownership-by-link-prop-tag.md) |
| Persistence | **Auto-persist** every in-app edit; external links load **"unsaved"** | [0009](docs/adr/0009-auto-persist-unsaved-external.md) |
| UI | **Board of per-sink columns** + Sources palette; not a patchbay | [0010](docs/adr/0010-sink-column-board-ui.md) |
| Process | **Single-instance** (unix-socket reveal); tray + systemd always-on | [0005](docs/adr/0005-single-instance-unix-socket.md) |

## Key technical facts (verified live — load-bearing)

- **Fan-out is real.** Spotify `output_FL/FR` were linked to *both* Ryzen analog (59) *and* MusicSink (87) at once. A single-valued `target.object` can't express that, which is why the metadata-redirect approach was tried and rejected ([0001](docs/adr/0001-metadata-redirect-for-the-move.md)→[0002](docs/adr/0002-pure-pw-link-fan-out.md)).
- **WirePlumber doesn't keep re-asserting the default link** once explicit links exist (Spotify had zero link to the default headset). So `pw-link` routing is stable; a Suppression only re-deletes WirePlumber's link **once per stream relaunch**, not in a loop.
- **Link objects carry `.info.props`; `pw-link -p '{…}'` sets arbitrary props** (verified flag; `-p` is properties, `-P` is passive). WP links carry `client.id`; `pw-link` links carry `object.linger` — but *neither distinguishes our links from the user's*, so we need the `autoroute.managed` tag. **Confirm the tag round-trips through `pw-dump` in milestone 1.**
- **App streams are indistinguishable by stable keys within one app.** Zen exposes 3 `Stream/Output/Audio` nodes, all `application.name=Zen`, `media.name="Home / X"`, `binary=zen-bin`, differing only by ephemeral id → per-tab routing is impossible; match at **app granularity**.
- **Targets can be ephemeral too.** Discord's `recStream` (`WEBRTC VoiceEngine`, `Stream/Input/Audio`) appears per call and is matched by stable key exactly like a source — hence symmetric rules.
- `pw-dump` is authoritative JSON state; `pw-mon` is a debounced "something changed, reload" **trigger only** (never parsed for semantics).

## Architecture: single process, three layers

`AutoRoute.App` builds a `Microsoft.Extensions.Hosting` host. A `RoutingWorker : BackgroundService` runs the reconcile loop; the Avalonia UI + tray live in the same host. `ShutdownMode = OnExplicitShutdown` — closing the window only **hides** it; the watcher and tray keep running; tray **Quit** is the only stop. One shared in-memory graph feeds both UI and reconciler → no IPC, edits apply instantly. A **single-instance** guard (unix socket) guarantees exactly one host.

```
AutoRoute.sln
├── src/AutoRoute.PipeWire/   # interop lib (no UI deps): graph model + CLI drivers
│   ├── Models/  PwNode, PwPort, PwLink (incl. props), PwGraph, PortDirection
│   ├── Process/ ProcessRunner, LongRunningProcess
│   ├── PwDumpReader.cs        # pw-dump -> PwGraph (System.Text.Json)
│   ├── PwMonMonitor.cs        # pw-mon -> debounced change signal (+ PollingGraphMonitor fallback)
│   ├── PwLinker.cs            # pw-link connect/disconnect by numeric port ID; stamps autoroute.managed
│   ├── ChannelMapper.cs       # FL->FL/FR->FR pairing; MONO fan-out; surround guard
│   └── PwGraphService.cs      # facade: owns snapshot, raises GraphUpdated
├── src/AutoRoute.Engine/      # rules + persistence (depends on PipeWire)
│   ├── Model/ MatchCriteria, Predicate, Rule(Positive), Suppression, ProtectedMatch, RulesDocument
│   ├── RuleStore.cs           # rules.json auto-save (atomic), hot-reload
│   ├── RuleMatcher.cs         # node -> matching source/target/protected matchers
│   └── Reconciler.cs          # idempotent desired-vs-actual diff (automation core)
├── src/AutoRoute.App/         # Avalonia MVVM GUI + tray + host
│   ├── Program.cs App.axaml(.cs)         # single-instance socket, tray, OnExplicitShutdown, --background
│   ├── Hosting/ RoutingWorker.cs         # always-on watcher (BackgroundService)
│   ├── ViewModels/ Board, SinkColumn, SourceCard, SourcesPalette, Filter
│   ├── Views/ BoardView, SinkColumnView, SourceCardView, PaletteView
│   └── Behaviors/ DragSourceBehavior, DropTargetBehavior
└── tests/AutoRoute.Tests/     # xUnit: pw-dump parsing, matcher, reconciler diff, tag round-trip
    └── fixtures/ pw-dump.sample.json, pw-mon.sample.txt
```

*(Note vs the original plan: `ManagedLinkLedger` is dropped ([0004](docs/adr/0004-ownership-by-link-prop-tag.md)); `pw-metadata` routing is dropped ([0002](docs/adr/0002-pure-pw-link-fan-out.md)); the source/target lists become the board.)*

## PipeWire layer (`AutoRoute.PipeWire`)

**Models** — `PwNode(Id, NodeName, Description, MediaClass, ApplicationName, ProcessBinary, MediaName, Ports…)` with `IsDraggableSource => HasOutputPorts` and `IsDropTarget => HasInputPorts`; `PwPort(Id, NodeId, Direction, PortName, Channel, PortIndex)`; `PwLink(Id, OutNodeId, OutPortId, InNodeId, InPortId, State, Props)` — `Props` carries `autoroute.managed`/`autoroute.rule` when present; `PwGraph` with by-id/by-node indices.

**`PwDumpReader.LoadAsync`** — run `pw-dump`, stream-parse the JSON array, keep `type ∈ Node|Port|Link`, attach ports to owning node via `node.id`, capture each Link's `.info.props`. Tolerate null props (MIDI/DSP nodes have null `media.class`). Non-zero exit → `PwToolException`; malformed JSON → log + keep last-good snapshot (never crash the watcher).

**`PwMonMonitor`** (default) — spawn `pw-mon`; any line starting `added:`/`changed:`/`removed:` pushes into a ~250 ms debounce → raises `Changed`; consumer reloads via `PwDumpReader`. Auto-respawn with backoff if `pw-mon` dies and force a full reload. `PollingGraphMonitor` (`--poll`) diffs periodic `pw-dump`s as a fallback.

**`PwLinker`** — `ConnectAsync(outPortId, inPortId, ruleId)` → `pw-link -p '{"autoroute.managed":"true","autoroute.rule":"<id>"}' <out> <in>`; `DisconnectAsync(linkId | out,in)` → `pw-link -d …`. Always numeric IDs. Failures (port vanished mid-cycle) are logged and self-heal next snapshot. **Milestone 1 gates whether the custom props survive into `pw-dump`; if stripped, reintroduce a persisted ledger ([0004](docs/adr/0004-ownership-by-link-prop-tag.md)).**

**`ChannelMapper`** — pairs a source's output ports to a target's input ports by `audio.channel` (FL→FL, FR→FR); MONO source → fan out to FL+FR; unmatched surround channels left unlinked with a UI warning.

## Rule engine (`AutoRoute.Engine`)

**`~/.config/autoroute/rules.json`** — atomic save (temp + `File.Move`), `FileSystemWatcher` hot-reload, source-generated `JsonSerializerContext`. Written on **every** in-app edit (auto-persist, [0009](docs/adr/0009-auto-persist-unsaved-external.md)).

```jsonc
{ "version": 1,
  "rules": [                          // positive: keep linked
    { "id": "…", "name": "Games → GameSink", "enabled": true,
      "source": { "predicates": [ { "field": "ApplicationName", "op": "Equals", "value": "Zen" } ] },
      "target": { "predicates": [ { "field": "NodeName",        "op": "Equals", "value": "GameSink" } ] } },
    { "id": "…", "name": "EasyEffects → Discord", "enabled": true,
      "source": { "predicates": [ { "field": "NodeName",        "op": "Equals", "value": "Easy Effects Source" } ] },
      "target": { "predicates": [ { "field": "ApplicationName", "op": "Equals", "value": "Discord" } ] } }
  ],
  "suppressions": [                    // negative: keep UN-linked (deletes even unowned)
    { "id": "…", "source": { "predicates": [ { "field": "ApplicationName", "op": "Equals", "value": "Zen" } ] },
                 "target": { "predicates": [ { "field": "NodeName", "op": "Equals", "value": "alsa_output.usb-Logitech_PRO_X_2_…analog-stereo" } ] } }
  ],
  "protected": [                       // do-not-touch: overrides all
    { "id": "…", "match": { "predicates": [ { "field": "ApplicationName", "op": "Equals", "value": "Discord" } ] } }
  ] }
```

`field ∈ {ApplicationName, NodeName, ProcessBinary, MediaName, MediaClass}`, `op ∈ {Equals, Contains, Regex}`, predicates AND-ed. Both `source` and `target` are `MatchCriteria`, resolved to live nodes each cycle. For any pair, positive and suppression never coexist — the **latest user action wins**.

**`Reconciler`** (runs on every `GraphUpdated`, idempotent):
1. Build **desired** managed-link set `D`: for each enabled positive rule × `sourceMatch` nodes × `targetMatch` nodes → `ChannelMapper` pairs, tagged with `ruleId`. **Skip any pair whose source or target matches a Protected marker.**
2. Read **actual** set `A` from snapshot Link objects, split by `autoroute.managed` prop into *managed* vs *unowned*.
3. **Create** `D \ A` (skips existing → no duplicates). Guard: create a channel pair only when **both** ports exist (node-appears-before-ports race → deferred to next snapshot).
4. **Stale cleanup:** *managed* links ∉ `D` → disconnect.
5. **Suppressions:** for each suppression, delete any actual link (managed *or* unowned) matching `sourceMatch → targetMatch` — **unless** an endpoint is Protected.
6. **Never touch any other unowned link** (the user's manual patches stay put).
7. Each op try/catch; transient failures self-heal next snapshot.

## UI/UX (Avalonia MVVM) — the board

- **Board:** horizontally-scrollable **columns, one per Target** (a sink, or an app-input target like Discord). Column header = the Target; body = a scrollable list of **Source cards** feeding it. Each *managed* card ⇔ one positive Rule.
- **Sources palette:** every draggable Source — app streams (`Stream/Output/Audio`), capture devices (`Audio/Source`), sink monitors (off by default), and sinks-as-sources. Drag a card into a column to connect; the card **stays in the palette**, so it can drop into many columns → **fan-out**.
- **Card states:** **Managed** (accent + rule tooltip + "×"), **Manual** (neutral), **Unsaved** (badge; external link, not persisted; a "save" action writes a positive Rule from its endpoints), **Protected** (locked/pinned).
- **Disconnect:** remove a card → delete the Rule (managed) or write a **Suppression** (external/manual). **Protect:** mark a node/app "do not touch".
- **Existing-link display:** on first launch the board mirrors the whole live graph; external links show as **unsaved**.
- **Threading:** watcher off-UI-thread; marshal via `Dispatcher.UIThread.Post`; diff-merge into `ObservableCollection`s by node Id to avoid flicker.

## Process, tray, autostart

- **Single instance ([0005](docs/adr/0005-single-instance-unix-socket.md)):** first process binds `$XDG_RUNTIME_DIR/autoroute.sock` and owns the sole host/worker/tray. A later launch connects, sends "reveal window", and exits. Handle a stale socket (connect; on connection-refused, unlink + bind). `--background` = start hidden.
- **Tray:** Avalonia native `TrayIcon`/`NativeMenu` (Open, "Automation Enabled" checkbox, Quit). StatusNotifierItem/DBus — Plasma/Wayland native (validate icon renders early).
- **Autostart:** systemd user service `~/.config/systemd/user/autoroute.service`, `After=pipewire.service wireplumber.service`, `ExecStart=%h/.local/bin/AutoRoute --background`, `Restart=on-failure`; `systemctl --user enable --now autoroute.service`; logs via `journalctl --user -u autoroute`. (XDG `~/.config/autostart/*.desktop` as a documented alternative.)

## NuGet packages

`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Diagnostics` (Debug) 11.3.x; `CommunityToolkit.Mvvm` 8.4.x; `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.Logging.Console` 10.0.x; `System.Text.Json` (in-box, `JsonSerializerContext`). Tests: `xunit` + `Microsoft.NET.Test.Sdk`.

## Build / scaffold

```bash
export PATH=/usr/share/dotnet:$PATH
dotnet new install Avalonia.Templates
mkdir -p /home/jessie/Files/Dev/AutoRoute && cd /home/jessie/Files/Dev/AutoRoute
dotnet new sln -n AutoRoute
dotnet new classlib      -o src/AutoRoute.PipeWire -f net10.0
dotnet new classlib      -o src/AutoRoute.Engine   -f net10.0
dotnet new avalonia.mvvm -o src/AutoRoute.App
dotnet new xunit         -o tests/AutoRoute.Tests  -f net10.0
dotnet sln add (find src tests -name '*.csproj')
dotnet add src/AutoRoute.Engine reference src/AutoRoute.PipeWire
dotnet add src/AutoRoute.App    reference src/AutoRoute.Engine src/AutoRoute.PipeWire
# set <TargetFramework>net10.0</TargetFramework> everywhere, add packages above
dotnet build
dotnet run --project src/AutoRoute.App                 # window
dotnet run --project src/AutoRoute.App -- --background  # tray-only
```

## Implementation order (build sequence — everything ships in v1)

Ordered to **derisk the load-bearing unknowns first**; v1 is not "finished" until all of it is done.

1. **PipeWire read path:** models + `PwDumpReader` + `PwGraphService`; unit-test against a captured `pw-dump.sample.json`. Print the graph.
2. **Ownership gate ([0004](docs/adr/0004-ownership-by-link-prop-tag.md)):** `PwLinker` connect/disconnect by numeric ID with the `autoroute.managed` tag; **empirically confirm the tag round-trips through `pw-dump`.** If not → reintroduce a persisted ledger before proceeding.
3. **Reconciler core (positive rules only)** + `RuleStore` auto-persist. **Prove the flagship loop:** route an app once, quit & relaunch it (new node IDs), link reappears untouched.
4. **Live monitor:** `PwMonMonitor` debounced reload + respawn; polling fallback; confirm the UI/reconcile updates when a stream starts/stops.
5. **Signed rules:** Suppressions (delete matching links incl. unowned) + Protected exclusions + precedence.
6. **Board UI:** sink columns + Sources palette + drag-drop behaviors + card states (managed/manual/unsaved/protected) + save/protect actions + filters.
7. **Always-on:** `RoutingWorker` + single-instance socket + `--background` + tray + systemd unit.

## Verification (end-to-end)

1. Route **Zen → GameSink** and **EasyEffects Source → Discord** in the board; add a **Suppression** for Zen → the default headset.
2. Start browser/game audio and join a Discord call. Confirm auto-linking (read-only):
   ```bash
   # Zen now feeds GameSink (89):
   pw-dump | jq -r '.[]|select(.type=="PipeWire:Interface:Link")|select(.info["input-node-id"]==89)'
   # our links carry the ownership tag:
   pw-dump | jq -r '.[]|select(.type=="PipeWire:Interface:Link")|select(.info.props["autoroute.managed"]=="true")|.id'
   ```
3. **Persistence test (flagship):** close & reopen the browser (new node IDs); without touching the GUI, confirm managed links reappear and the **suppressed** headset link stays gone (`journalctl --user -u autoroute -f`).
4. **Fan-out:** one source dropped into two sink columns → both links present.
5. **Protected:** mark Discord "do not touch"; confirm no rule/suppression ever mutates its links.

## Risks & mitigations

- **Tag stripped by `pw-dump`** → milestone-2 gate; fall back to a persisted ledger ([0004](docs/adr/0004-ownership-by-link-prop-tag.md)).
- **Suppression vs WirePlumber** → one delete per stream relaunch (WP doesn't re-assert continuously); debounce; future `node.autoconnect=false` to remove even that ([0007](docs/adr/0007-signed-rules-and-suppressions.md)).
- **Node-before-ports race** → create a pair only when both ports exist; retry next snapshot.
- **Ambiguous port names** → link by numeric ID (core).
- **Mono/surround** → `ChannelMapper` policies + UI warning for unmatched channels.
- **Two reconcilers** → single-instance socket ([0005](docs/adr/0005-single-instance-unix-socket.md)).
- **Accidentally deleting the user's manual patches** → reconciler only deletes *managed* or *suppressed* links; every other unowned link is never touched ([0007](docs/adr/0007-signed-rules-and-suppressions.md)).

## Deferred to v2

- **App-managed virtual sinks** — a button to create/remove null sinks, retiring the static `virtual-sinks.conf` ([0011](docs/adr/0011-app-managed-virtual-sinks.md), **accepted**; mechanism settled as hybrid). **Build spec: [PLAN.v2.md](PLAN.v2.md).**
