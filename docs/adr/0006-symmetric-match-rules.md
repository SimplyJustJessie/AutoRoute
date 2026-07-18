# Symmetric source→target match rules; independent single hops

**Context.** A rule must tolerate an ephemeral endpoint on *either* side: an ephemeral source into a fixed sink (`game stream → GameSink`), or a fixed source into an ephemeral target (`Easy Effects Source → Discord's recStream`, which is reborn with a new node ID every call). The plan's asymmetric model — predicate-matched source, static `sinkNodeName` targets — cannot express an ephemeral target. Real chains (`mic → EasyEffects filters → Discord`) also contain intermediate hops owned by other apps.

**Decision.** A Rule is a **symmetric pair of stable-key match-criteria**, `sourceMatch → targetMatch`, both resolved to live nodes on every reconcile; the reconciler Links every node matching `sourceMatch` to every node matching `targetMatch` (channel-paired). A fixed sink is just the degenerate case where `targetMatch` is a `node.name` equality. AutoRoute makes only **independent single-hop links** and does **not** model or own multi-hop chains — intermediate hops belong to the apps that own them (EasyEffects' internal filter graph, OBS's monitor capture).

**Consequences.** Ephemeral endpoints are handled uniformly on either side. Drag-drop derives a default matcher from *both* the dragged Source and the drop Target. There is no chain engine; the original plan's "full chain" framing is dropped.
