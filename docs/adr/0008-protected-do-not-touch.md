# Protected nodes ("do not touch"): explicit exclusions for externally-managed routing

**Context.** Some routing is owned and maintained by another tool, not AutoRoute. The `mic → EasyEffects → Discord` chain, for instance, is set up and kept alive by EasyEffects itself. AutoRoute must be able to stay entirely out of such routing so that no Rule, no Suppression, and no future broad matcher ever disturbs it — and so the user can trust that a node handled elsewhere is off-limits.

**Decision.** A **Protected** marker is a stable-key match (e.g. `application.name = Discord`, or a specific `node.name`) identifying nodes AutoRoute must never touch. The reconciler never creates, deletes, or suppresses any link whose source or target matches a Protected marker. Precedence is absolute: **Protected > Suppression > positive Rule**. Protection is expressed by stable keys, so it survives the node's ephemeral reincarnations.

**Consequences.** Gives the user a hard, durable boundary against AutoRoute interfering with routing owned by EasyEffects, OBS, or WirePlumber policy. Node/app-level granularity (protecting a node protects every link touching it) covers the stated case; per-link protection can be added later if a need appears. A Protected node is also a natural candidate to lock/pin in the UI so it can't be edited by accident.
