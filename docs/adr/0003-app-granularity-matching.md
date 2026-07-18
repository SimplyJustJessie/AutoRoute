# Match at app granularity; per-tab / per-stream routing is out of scope

**Context.** Rules match streams by keys that stay stable across relaunches (node IDs are ephemeral and must never be persisted). Verified live that a browser (Zen) exposes several `Stream/Output/Audio` nodes that are byte-identical on *every* stable key — `node.name=Zen`, `application.name=Zen`, `media.name="Home / X"`, `application.process.binary=zen-bin` — differing only by ephemeral node ID. No stable key can therefore isolate one browser stream (or tab) from another.

**Decision.** Rules match at **application granularity** (`application.name`, falling back to `application.process.binary`). All of a matched app's streams are routed together as a unit. Per-tab / per-stream isolation within one app is out of scope; the user confirms browser tabs should count as one. `media.name`/`Regex` remains available as a best-effort field for apps that *do* vary it meaningfully — but never for the browser.

**Consequences.** Stops a future contributor from "fixing" the absence of per-tab routing — it is impossible without persisting ephemeral IDs, not an oversight. Keeps the match model simple: one rule, one app, all its streams.
