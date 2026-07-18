# Auto-persist in-app edits; external links load "unsaved"

**Context.** Routing done *through* AutoRoute should be remembered automatically, with no explicit Save step. Links that exist in the graph but were made outside the app (Helvum, WirePlumber's default policy) should be visible — AutoRoute mirrors reality — but not silently claimed as AutoRoute's own.

**Decision.** Every routing edit made *inside* the app (connect → positive Rule, disconnect → Suppression, mark → Protected) is auto-persisted to `rules.json` immediately, via atomic write. There is **no** global Save / document. A link present in the graph that AutoRoute did not create and that no rule describes is shown marked **unsaved**: it is not persisted and will not be re-established after a relaunch until the user acts on it in the app (e.g. a per-item "save," which writes a positive Rule derived from the link's endpoints).

**Consequences.** Zero-friction persistence — the common path (route it once in the app, it sticks) needs no save step. The unsaved badge makes explicit which live routing AutoRoute will versus won't reproduce, so nothing external is claimed by surprise. `rules.json` is written on every edit; writes are atomic (temp + rename) so a crash mid-write can't corrupt the ruleset.
