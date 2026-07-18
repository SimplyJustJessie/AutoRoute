# AutoRoute — Automated PipeWire Routing Manager

> `AutoRoute` is a placeholder name — rename freely. Root namespace `AutoRoute`, config dir `~/.config/autoroute/`, project root `/home/jessie/Files/Dev/AutoRoute/`.

## Context

Today, WirePlumber's stock policy dumps every application audio stream onto the default sink (your Logitech PRO X 2 headset, node id 55). You have custom null-sinks — `GameSink` (Game, id 89), `MusicSink`, `DiscordSink`, `DesktopSink` — declared in `~/.config/pipewire/pipewire-pulse.conf.d/virtual-sinks.conf` (always present). To get game audio into `GameSink` you currently open **Helvum** *every launch* and hand-link the game's stream, because PipeWire node IDs are ephemeral (regenerated each launch), so a one-time manual link never persists.

This app automates that. It watches the PipeWire graph, lets you assign sources to sinks via drag-and-drop **once**, and a background watcher re-applies those assignments automatically whenever a matching app/stream reappears — even with the window closed. It handles the **full chain** (game → GameSink → headset), not just the first hop.

## Locked decisions

| Decision | Choice |
|---|---|
| Stack | **C# + Avalonia UI on .NET 10** (SDK 10.0.110 installed). MVVM. |
| Automation | **Single-process GUI + system tray + background watcher**; auto-reapplies saved rules. |
| Source list scope | App output streams + capture sources + **sink monitors** (full chain). Filterable. |
| UI model | Drag a source (has output ports) onto a target sink (has input ports); source **stays in list** (one-to-many); **existing links auto-detected & displayed**. |
| PipeWire access | Drive CLI tools — no native .NET bindings exist. `pw-dump` = state, `pw-mon` = change trigger, `pw-link` = connect/disconnect. |

## Key technical facts (verified live — load-bearing)

- **Link by numeric port ID, never by name.** The running Zen browser exposes **3** `Stream/Output/Audio` nodes all sharing the port name `Zen:output_FL` — names are ambiguous; port IDs are unique.
- **`pw-mon` output is indentation/`*`-marked text, not JSON.** Do not parse it for semantics — use it only as a debounced "something changed, reload" signal; take authoritative state from `pw-dump` (clean JSON).
- **Existing-link detection** comes from `pw-dump` `Link` objects (`output-node-id/output-port-id/input-node-id/input-port-id/state`), not `pw-link -l` text.
- A null sink (GameSink id 89) has **input** ports `playback_FL/FR` (ids 100/101) *and* **monitor output** ports `monitor_FL/FR` (ids 102/103) — which is why sinks are also draggable sources for the forward hop. GameSink currently has **zero** links (chain not set up).
- Stable match keys for persistence: `application.name` (Zen streams → `"Zen"`), fallback `node.name`, optional `media.name` regex. Never persist node IDs.

## Architecture: single process, three layers

`AutoRoute.App` builds a `Microsoft.Extensions.Hosting` host. A `RoutingWorker : BackgroundService` runs the reconcile loop; the Avalonia UI + tray icon live in the same host. `ShutdownMode = OnExplicitShutdown` — closing the window only **hides** it; the watcher and tray keep running; tray **Quit** is the only stop. One shared in-memory graph feeds both UI and reconciler → no IPC, rule edits apply instantly.

```
AutoRoute.sln
├── src/AutoRoute.PipeWire/   # interop lib (no UI deps): graph model + CLI drivers
│   ├── Models/  PwNode, PwPort, PwLink, PwGraph, PortDirection
│   ├── Process/ ProcessRunner, LongRunningProcess
│   ├── PwDumpReader.cs        # pw-dump -> PwGraph (System.Text.Json)
│   ├── PwMonMonitor.cs        # pw-mon -> debounced change signal (+ PollingGraphMonitor fallback)
│   ├── PwLinker.cs            # pw-link connect/disconnect by numeric port ID
│   ├── ChannelMapper.cs       # FL->FL/FR->FR pairing; MONO fan-out; surround guard
│   └── PwGraphService.cs      # facade: owns snapshot, raises GraphUpdated
├── src/AutoRoute.Engine/      # rules + persistence (depends on PipeWire)
│   ├── Model/ Rule, MatchCriteria, TargetRef, RulesDocument
│   ├── RuleStore.cs           # rules.json load/atomic-save, hot-reload
│   ├── RuleMatcher.cs         # node -> matching rules
│   ├── Reconciler.cs          # idempotent desired-vs-actual diff (automation core)
│   └── ManagedLinkLedger.cs   # ownership record for stale-link cleanup
├── src/AutoRoute.App/         # Avalonia MVVM GUI + tray + host
│   ├── Program.cs App.axaml(.cs)         # tray, OnExplicitShutdown, --background flag
│   ├── Hosting/ RoutingWorker.cs         # always-on watcher (BackgroundService)
│   ├── ViewModels/ MainWindow, SourceNode, TargetSink, Connection, RuleEditor, Filter
│   ├── Views/ MainWindow, SourceListView, TargetListView, RuleEditorView
│   └── Behaviors/ DragSourceBehavior, DropTargetBehavior
└── tests/AutoRoute.Tests/     # xUnit: pw-dump parsing, matcher, reconciler diff
    └── fixtures/ pw-dump.sample.json, pw-mon.sample.txt
```

## PipeWire layer (`AutoRoute.PipeWire`)

**Models** — `PwNode(Id, NodeName, Description, MediaClass, ApplicationName, ProcessBinary, MediaName, Ports…)` with `IsDraggableSource => HasOutputPorts` and `IsDropTarget => HasInputPorts`; `PwPort(Id, NodeId, Direction, PortName, Channel, PortIndex)`; `PwLink(Id, OutNodeId, OutPortId, InNodeId, InPortId, State)`; `PwGraph` with by-id/by-node indices.

**`PwDumpReader.LoadAsync`** — run `pw-dump`, stream-parse the JSON array with `JsonDocument`, keep `type` in `Node|Port|Link`, attach ports to owning node via `node.id`. Tolerate null props (MIDI/DSP nodes have null `media.class`). Non-zero exit → `PwToolException`; malformed JSON → log + keep last-good snapshot (never crash the watcher).

**`PwMonMonitor`** (default) — spawn `pw-mon`, read stdout lines; any line starting `added:`/`changed:`/`removed:` pushes into a ~250 ms debounce → raises `Changed`; consumer reloads via `PwDumpReader`. Auto-respawn with backoff if `pw-mon` dies (PipeWire restart) and force a full reload. `PollingGraphMonitor` (`--poll`) diffs periodic `pw-dump`s as a fallback.

**`PwLinker`** — `ConnectAsync(outPortId, inPortId, ruleId)` → `pw-link -p '{"autoroute.managed":"true","autoroute.rule":"<id>"}' <out> <in>`; `DisconnectLinkAsync(linkId)` / `DisconnectAsync(out,in)` → `pw-link -d …`. Always numeric IDs. Failures (port vanished mid-cycle) are logged and self-heal next snapshot. *(Verify at build time whether custom link props survive into `pw-dump`; if stripped, ownership falls back to `ManagedLinkLedger`.)*

**`ChannelMapper`** — pairs a source's output ports to a sink's input ports by `audio.channel` (FL→FL, FR→FR); MONO source → fan out to FL+FR; unmatched surround channels left unlinked with a UI warning.

## Rule engine (`AutoRoute.Engine`)

**`~/.config/autoroute/rules.json`** — atomic save (temp + `File.Move`), `FileSystemWatcher` hot-reload, source-generated `JsonSerializerContext`.

```jsonc
{ "version": 1, "rules": [
  { "id": "…", "name": "Games → GameSink", "enabled": true, "exclusive": true,
    "match": { "predicates": [ { "field": "ApplicationName", "op": "Equals", "value": "Zen" } ] },
    "targets": [ { "sinkNodeName": "GameSink" } ] },
  { "id": "…", "name": "GameSink monitor → Headset", "enabled": true, "exclusive": false,
    "match": { "predicates": [ { "field": "NodeName", "op": "Equals", "value": "GameSink" } ] },
    "targets": [ { "sinkNodeName": "alsa_output.usb-Logitech_PRO_X_2_LIGHTSPEED_…analog-stereo" } ] } ] }
```

`field ∈ {ApplicationName, NodeName, ProcessBinary, MediaName, MediaClass}`, `op ∈ {Equals, Contains, Regex}`, predicates AND-ed. Targets reference sinks by **stable `node.name`**, resolved to a live node each cycle. The two rules above are the canonical full chain.

**`Reconciler`** (runs on every `GraphUpdated`, idempotent):
1. Build **desired** link set `D`: for each source node × matching enabled rule × resolvable target sink → `ChannelMapper` pairs, tagged with `ruleId`.
2. Read **actual** set `A` from snapshot Link objects.
3. **Create** `D \ A` (skips existing → no duplicates).
4. **Stale cleanup**: links AutoRoute owns (managed prop / ledger) that are ∉ `D` → disconnect. Never touch links it doesn't own (protects manual patches).
5. **Exclusive** rules: disconnect the matched source's links to sinks not in `targets` — this is what *moves* Zen off the default headset onto GameSink instead of duplicating audio.
6. Guard: create a channel pair only when **both** ports exist (handles node-appears-before-ports race → deferred to next snapshot). Each op try/catch; transient failure self-heals.

**Drag creates rules**: drop source S on sink T → if a rule already matches S, **add** T to its targets (one-to-many); else create a rule with a default predicate from S (`ApplicationName` else `NodeName`) targeting T. Save → immediate reconcile.

## UI/UX (Avalonia MVVM)

- **Layout**: left = filterable source list (any node w/ output ports); right = target sinks (any node w/ input ports). Each source card shows current connections as chips.
- **Drag-drop**: source cards call `DragDrop.DoDragDrop(…, DragDropEffects.Link)` carrying a `SourceDragPayload{NodeId, ApplicationName, NodeName}` under custom format `"autoroute/source"`; sinks set `AllowDrop`, handle `DragOver` (effect=Link, highlight) + `Drop` (→ `CreateAssignmentCommand`). Wrapped in `DragSourceBehavior`/`DropTargetBehavior` attached behaviors.
- **One-to-many "stays in list"**: source list binds to the live graph snapshot, never a mutable "unassigned" collection. Dropping only writes a rule; the card remains and gains a connection chip.
- **Existing-link display**: each `SourceNodeViewModel.Connections` is built from snapshot Link objects; `ConnectionViewModel.Kind ∈ {ManagedByRule, Manual}` — managed chips accent-colored with rule tooltip + "×"; manual chips neutral with an "adopt into rule" action.
- **Filtering** (`FilterViewModel`): toggle chips — App streams (`Stream/Output/Audio`), Microphones (`Audio/Source[/Virtual]`), Sink monitors (`Audio/Sink` outputs, **off by default** to reduce clutter) + text search.
- **Threading**: watcher off-UI-thread; marshal via `Dispatcher.UIThread.Post`, diff-merge into `ObservableCollection`s by node Id to avoid flicker.

## Tray + autostart

- **Tray**: Avalonia native `TrayIcon`/`NativeMenu` in `App.axaml` (Open, "Automation Enabled" checkbox, Quit). Uses StatusNotifierItem/DBus — Plasma/Wayland supports it natively (validate icon renders early).
- **Autostart** (recommended): systemd user service `~/.config/systemd/user/autoroute.service`, `After=pipewire.service wireplumber.service`, `ExecStart=%h/.local/bin/AutoRoute --background`, `Restart=on-failure`; enable via `systemctl --user enable --now autoroute.service`; logs via `journalctl --user -u autoroute`. (Ship an XDG `~/.config/autostart/*.desktop` as a documented alternative.)

## NuGet packages

`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Diagnostics` (Debug) — 11.3.x (runs on the .NET 10 runtime); `CommunityToolkit.Mvvm` 8.4.x; `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.Logging.Console` 10.0.x; `System.Text.Json` (in-box, use `JsonSerializerContext`). Tests: `xunit` + `Microsoft.NET.Test.Sdk`.

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
# set <TargetFramework>net10.0</TargetFramework> in every csproj, add packages above
dotnet build
dotnet run --project src/AutoRoute.App                 # window
dotnet run --project src/AutoRoute.App -- --background  # tray-only
```

## Suggested implementation order (milestones)

1. **PipeWire read path**: models + `PwDumpReader` + `PwGraphService`; unit-test against a captured `pw-dump.sample.json`. Print the graph.
2. **Tray spike**: bare Avalonia app + tray icon + hide-on-close on Wayland/KDE (de-risk early).
3. **Linker + ChannelMapper**: `PwLinker` connect/disconnect by numeric ID; verify a link appears/disappears in `pw-link -l`.
4. **Live monitor**: `PwMonMonitor` debounced reload; confirm UI updates when a stream starts/stops.
5. **UI**: source/target lists, drag-drop behaviors, connection chips, filters.
6. **Rule engine**: `RuleStore` + `RuleMatcher` + `Reconciler` + `ManagedLinkLedger`; wire drag → rule → reconcile.
7. **`RoutingWorker`** always-on watcher + `--background` + systemd unit.

## Verification (end-to-end)

1. Run the app; drag **Zen** onto **GameSink**; enable the "Sink monitors" filter and drag **GameSink** onto the **PRO X 2 headset**.
2. Start browser/game audio. Confirm auto-linking (read-only):
   ```bash
   # stream now feeds GameSink (89):
   pw-dump | jq -r '.[]|select(.type=="PipeWire:Interface:Link")|select(.info["input-node-id"]==89)|"\(.info["output-node-id"]):\(.info["output-port-id"]) -> GameSink:\(.info["input-port-id"])"'
   # GameSink monitor (102/103) forwarded to headset (55):
   pw-dump | jq -r '.[]|select(.type=="PipeWire:Interface:Link")|select(.info["output-node-id"]==89)|"GameSink:\(.info["output-port-id"]) -> node\(.info["input-node-id"]):\(.info["input-port-id"])"'
   pw-link -l | grep -A2 GameSink
   ```
3. **Persistence test**: close & reopen the browser (new node IDs); without touching the GUI, confirm links reappear (`journalctl --user -u autoroute -f` + rerun the jq checks). Proves the stable-match reconciler.

## Risks & mitigations (summary)

- **Ambiguous port names** → link by numeric ID (already core).
- **pw-mon fragility** → trigger-only, `pw-dump` authoritative, auto-respawn, polling fallback.
- **Node-before-ports race** → create a pair only when both ports exist; retry next snapshot.
- **WirePlumber duplicating to default sink** → `exclusive` rules move the stream (expect <1-frame flap).
- **Mono/surround** → `ChannelMapper` policies + UI warning for unmatched channels.
- **Stale managed links / rule deletion** → managed-prop tag + `ManagedLinkLedger`; never touch unowned links.
- **Custom link props maybe stripped** → verify empirically; ledger is the fallback ownership record.
