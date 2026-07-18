# App-managed virtual sinks

> **Status: proposed — v2.** Not part of v1. Recorded to preserve the decision and its open fork.

**Context.** Today the null sinks (GameSink, MusicSink, DiscordSink, DesktopSink) are declared statically in `~/.config/pipewire/pipewire-pulse.conf.d/virtual-sinks.conf` and created at launch by that config. The user wants to create and remove virtual sinks from a button inside AutoRoute, retiring that static config — so AutoRoute would own the **virtual-sink lifecycle**, not only the links between existing nodes.

**Decision (proposed).** AutoRoute gains UI to create and delete virtual (null) sinks. **Open fork — the creation mechanism:**
- *Declarative:* write a `pipewire-pulse` conf.d drop-in and reload; PipeWire creates the sinks, which persist independently of whether AutoRoute is running (matches today's "always present" behaviour). The genuine script replacement.
- *Imperative:* create sinks at runtime via `pactl`/`pw-cli` and recreate them after a PipeWire restart from the always-on watcher; no config editing, but sink existence becomes dependent on AutoRoute running — a regression from today.
- *Hybrid (current lean):* write the drop-in for persistence **and** load immediately for instant effect.

**Consequences.** Removes the manual config/script and makes sink management a first-class in-app action. Whether virtual sinks remain AutoRoute-independent depends on which mechanism is chosen — to be settled when v2 begins.
