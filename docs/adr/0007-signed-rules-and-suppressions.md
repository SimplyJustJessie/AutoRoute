# Signed rules: positive Links and negative Suppressions

**Context.** The user drives AutoRoute as a *persistent Helvum*: every manual edit in the graph view must survive relaunch in both directions — a connection they make should be re-established when the nodes reappear, and a disconnection they make should **stay** disconnected. The earlier add-only stance could re-establish a link but could never keep one broken (WirePlumber re-links a fresh stream to the default sink on every launch).

**Decision.** A rule is **signed**:
- A **positive rule** keeps a matched `sourceMatch → targetMatch` linked — the reconciler creates and maintains a Managed link (tagged `autoroute.managed`).
- A **Suppression** keeps a matched pair *unlinked* — the reconciler deletes any matching link on every cycle, **including unowned links** such as WirePlumber's default.

A manual connect creates/updates a positive rule; a manual disconnect creates a Suppression. For a given pair the **latest action wins** (connecting clears a Suppression; disconnecting overrides a positive rule). The reconciler deletes a link **only when** it is a stale Managed link *or* matches an active Suppression; every other unowned link (the user's untouched manual patches) is still never touched.

**Consequences.** This reverses ADR-0002's blanket "never delete unowned links," but keeps a precise guarantee: the reconciler only ever deletes links the user explicitly created-via-rule or explicitly suppressed. Enforcing a Suppression re-deletes the link whenever it reappears — e.g. WirePlumber re-adding the default link when a stream relaunches — a bounded flap. Live evidence shows WirePlumber does not aggressively re-assert, so in practice this is one deletion per stream relaunch; enforcement is debounced. A future optimization could set `node.autoconnect=false` via `pw-metadata` to stop WirePlumber creating the default link at all, removing even that single flap.
