# AutoRoute — Frozen contracts (Wave 1 → Wave 2)

**Status:** Wave 1 (Foundation) complete and GREEN. This document is the public surface the
Engine and UI teammates build against. It **compiles today** — every type and interface below
exists as real code so Wave 2 can reference it immediately.

**Rule of the road:** the Engine teammate may freely change *internals* of the stub classes
(`RuleStore`, `RuleMatcher`, `Reconciler`). If you change a **public** signature listed here,
update this file in the same change so the UI teammate isn't broken silently.

> **Ownership gate: PASS.** The `autoroute.managed` link prop set via `pw-link -p` round-trips
> through `pw-dump` (verified live, Milestone 2 — see bottom). ADR-0004 stands: the prop tag is
> the sole ownership record. **No persisted ledger is needed.** Note pw-dump returns the value
> as a JSON boolean `true` (not the string `"true"`); the parser normalizes both — always use
> `PwLink.IsManaged`, never read `Props["autoroute.managed"]` directly.

---

## Project layout

```
src/AutoRoute.PipeWire/   # interop lib, no UI deps — Wave 1, DONE
src/AutoRoute.Engine/     # rules + reconcile — models+interfaces frozen, impls are STUBS
src/AutoRoute.App/        # Avalonia MVVM host — scaffold only
tests/AutoRoute.Tests/    # xUnit — 24 passing (parse/mapper/linker/gate)
```

References: `Engine → PipeWire`; `App → Engine, PipeWire`; `Tests → Engine, PipeWire`.
Target framework `net10.0` everywhere. Root namespace `AutoRoute`.

---

## 1. PipeWire models  (`AutoRoute.PipeWire.Models`)

All immutable `record`s. Ephemeral numeric ids are valid only within one snapshot; never persist them.

### `PortDirection` — `Models/PortDirection.cs`
```csharp
enum PortDirection { Input, Output }
```

### `PwPort` — `Models/PwPort.cs`
```csharp
sealed record PwPort(int Id, int NodeId, PortDirection Direction,
                     string? PortName, string? Channel, int PortIndex)
{
    bool IsOutput;   // Direction == Output
    bool IsInput;    // Direction == Input
}
```
`Channel` is `audio.channel` (e.g. `FL`, `FR`); `null` for MIDI/DSP/control ports.

### `PwNode` — `Models/PwNode.cs`
```csharp
sealed record PwNode(int Id, string? NodeName, string? Description, string? MediaClass,
                     string? ApplicationName, string? ProcessBinary, string? MediaName,
                     IReadOnlyList<PwPort> Ports)
{
    IEnumerable<PwPort> OutputPorts, InputPorts;
    bool HasOutputPorts, HasInputPorts;
    bool IsDraggableSource;   // == HasOutputPorts  (can be dragged FROM)
    bool IsDropTarget;        // == HasInputPorts   (can be dropped ONTO)
}
```
The five stable-key match fields are `ApplicationName`, `NodeName`, `ProcessBinary`, `MediaName`,
`MediaClass` — these map 1:1 to `Engine.Model.Field`. A sink is both a target (playback inputs)
**and** a source (monitor outputs), so `IsDraggableSource` and `IsDropTarget` can both be true.

### `PwLink` — `Models/PwLink.cs`
```csharp
sealed record PwLink(int Id, int OutNodeId, int OutPortId, int InNodeId, int InPortId,
                     string? State, IReadOnlyDictionary<string,string> Props)
{
    const string ManagedPropKey = "autoroute.managed";
    const string RulePropKey    = "autoroute.rule";
    bool    IsManaged;   // Props[ManagedPropKey] == "true" (case-insensitive)
    string? RuleId;      // Props[RulePropKey] or null
}
```
`Props` is the verbatim `info.props` bag with scalar values stringified (JSON `true` → `"true"`).

### `PwGraph` — `Models/PwGraph.cs`
```csharp
sealed class PwGraph
{
    IReadOnlyDictionary<int,PwNode> NodesById;
    IReadOnlyDictionary<int,PwPort> PortsById;
    IReadOnlyDictionary<int,PwLink> LinksById;
    IReadOnlyDictionary<int,IReadOnlyList<PwPort>> PortsByNodeId;   // by-node index
    IReadOnlyCollection<PwNode> Nodes;  IReadOnlyCollection<PwLink> Links;  IReadOnlyCollection<PwPort> Ports;
    static PwGraph Empty;

    PwNode? Node(int id);  PwPort? Port(int id);  PwLink? Link(int id);
    IEnumerable<PwLink> ManagedLinks;    // IsManaged
    IEnumerable<PwLink> UnownedLinks;    // !IsManaged  (user/WirePlumber links — never touch except via Suppression)
    IEnumerable<PwLink> LinksForNode(int nodeId);
    bool HasLink(int outPortId, int inPortId);
}
```

---

## 2. PipeWire services  (`AutoRoute.PipeWire`)

### `IPwGraphService` — `IPwGraphService.cs`  (the shared snapshot owner)
```csharp
interface IPwGraphService
{
    PwGraph Current { get; }                               // latest snapshot (Empty until first load)
    event EventHandler<PwGraph> GraphUpdated;              // fires on a background thread
    Task StartAsync(CancellationToken ct = default);       // initial load + start monitor
    Task StopAsync();
    Task<PwGraph> RefreshAsync(CancellationToken ct = default);
}
```
Impl `PwGraphService(PwDumpReader, IGraphMonitor, ILogger?)`. **UI**: subscribe to `GraphUpdated`,
marshal to the UI thread yourself (`Dispatcher.UIThread.Post`). **Reconciler**: read `Current` /
subscribe to `GraphUpdated`. One shared instance feeds both — no IPC.

### `IPwLinker` — `IPwLinker.cs`
```csharp
readonly record struct LinkOpResult(bool Success, string? Error) { static Ok; static Fail(string); }

interface IPwLinker
{
    Task<LinkOpResult> ConnectAsync(int outPortId, int inPortId, string ruleId, CancellationToken ct = default);
    Task<LinkOpResult> DisconnectAsync(int linkId, CancellationToken ct = default);
    Task<LinkOpResult> DisconnectAsync(int outPortId, int inPortId, CancellationToken ct = default);
}
```
Impl `PwLinker(IProcessRunner, ILogger?)`. `ConnectAsync` stamps the `autoroute.managed` /
`autoroute.rule` tag. Never throws for a vanished port — returns `Fail`, reconciler self-heals
next snapshot. `static PwLinker.BuildManagedProps(string ruleId)` exposes the exact props JSON.

### `IGraphMonitor` — `IGraphMonitor.cs`
```csharp
interface IGraphMonitor : IAsyncDisposable
{
    event EventHandler Changed;                       // debounced "reload due" trigger
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
```
Two impls: `PwMonMonitor` (default; watches `pw-mon`, 250 ms debounce, auto-respawn w/ backoff)
and `PollingGraphMonitor(PwDumpReader, TimeSpan?, ILogger?)` (`--poll` fallback; diffs dumps).
pw-mon output is a bare trigger — **never parse it for semantics**.

### Supporting types (`AutoRoute.PipeWire` / `.Process`)
- `PwDumpReader(IProcessRunner, ILogger?)` — `Task<PwGraph> LoadAsync(ct)`, `static PwGraph Parse(string json)`,
  `PwGraph LastGood`. Non-zero pw-dump exit → `PwToolException`; malformed JSON → logs + returns `LastGood`.
- `ChannelMapper` (static, pure) — `ChannelPairing Map(PwNode source, PwNode target)` and
  `Map(IReadOnlyList<PwPort> outs, IReadOnlyList<PwPort> ins)`.
  `record PortPair(int OutPortId, int InPortId, string Channel)`;
  `record ChannelPairing(IReadOnlyList<PortPair> Pairs, IReadOnlyList<string> UnmatchedSourceChannels,
   IReadOnlyList<string> UnmatchedTargetChannels) { bool HasWarnings; }`.
  FL→FL / FR→FR; MONO source fans out to FL+FR; unmatched surround channels reported for a UI warning.
- `IProcessRunner` / `ProcessRunner` — `Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, bool throwOnNonZero=true, ct)`.
  `readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr) { bool Succeeded; }`.
- `LongRunningProcess(string file, string[] args, ILogger?)` — `LineReceived`/`Exited` events (drives pw-mon).
- `Debouncer(TimeSpan, Action)` — coalesces bursts into one callback.

---

## 3. Engine model  (`AutoRoute.Engine.Model`)  — matches `rules.json` exactly

All `record`s, `System.Text.Json` attributes freeze the on-disk key names.

```csharp
enum Field { ApplicationName, NodeName, ProcessBinary, MediaName, MediaClass }   // serialized by name
enum Op    { Equals, Contains, Regex }                                           // serialized by name

sealed record Predicate(Field Field, Op Op, string Value);            // {"field","op","value"}
sealed record MatchCriteria(IReadOnlyList<Predicate> Predicates)      // {"predicates":[...]}   AND-ed
    { static MatchCriteria Empty; }
sealed record Rule(string Id, string Name, bool Enabled,
                   MatchCriteria Source, MatchCriteria Target);       // {"id","name","enabled","source","target"}
sealed record Suppression(string Id, MatchCriteria Source, MatchCriteria Target);  // {"id","source","target"}
sealed record ProtectedMatch(string Id, MatchCriteria Match);         // {"id","match"}
sealed record RulesDocument(int Version, IReadOnlyList<Rule> Rules,
                            IReadOnlyList<Suppression> Suppressions,
                            IReadOnlyList<ProtectedMatch> Protected)   // {"version","rules","suppressions","protected"}
    { const int CurrentVersion = 1; static RulesDocument Empty; }
```
Files: `Model/Predicate.cs`, `Model/MatchCriteria.cs`, `Model/Rule.cs`, `Model/Suppression.cs`,
`Model/ProtectedMatch.cs`, `Model/RulesDocument.cs`.

---

## 4. Engine services  (`AutoRoute.Engine`)  — interfaces FROZEN, impls are STUBS

```csharp
interface IRuleStore {                                   // IRuleStore.cs  (impl RuleStore.cs — STUB)
    RulesDocument Current { get; }
    event EventHandler<RulesDocument> Changed;           // hot-reload
    Task<RulesDocument> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(RulesDocument document, CancellationToken ct = default);   // atomic
}

interface IRuleMatcher {                                 // IRuleMatcher.cs  (impl RuleMatcher.cs — STUB)
    bool Matches(MatchCriteria criteria, PwNode node);
    IEnumerable<PwNode> Resolve(MatchCriteria criteria, PwGraph graph);
}

interface IReconciler {                                  // IReconciler.cs  (impl Reconciler.cs — STUB)
    Task ReconcileAsync(PwGraph graph, RulesDocument rules, CancellationToken ct = default);
}
```
Stub bodies throw `NotImplementedException` (see the `// Wave 2` markers). `Reconciler` ctor
already takes `(IPwLinker, IRuleMatcher)` so DI wiring compiles now.

---

## 5. Ownership gate evidence (Milestone 2 — PASS)

Two isolated null sinks (`autoroute_gate_a`/`_b`, silent, no producer) were created; a link was
made `A.monitor.FL → B.playback.FL` with `pw-link -w -p '{"autoroute.managed":"true","autoroute.rule":"gate-test"}'`;
`pw-dump` then showed on that Link (id 218):

```json
"props": { "autoroute.managed": true, "autoroute.rule": "gate-test", ... }
```

The tag survived. Link + both modules were then removed and the system verified clean. The real
captured Link object is committed at `tests/AutoRoute.Tests/fixtures/pw-dump.gate-tagged-link.json`
and asserted by `PwDumpReaderResilienceTests.Reads_ownership_tag_from_REAL_gate_dump_...`.
