# Single-instance via a unix-socket reveal handoff

**Context.** Three start paths overlap: the systemd `--background` service (a full host + `RoutingWorker`), a GUI launch (another host + `RoutingWorker`), and hide-on-close keeping instances alive. The ordinary flow — service autostarts at login, user later clicks the app to edit a rule — yields two processes reconciling the same graph: double `pw-link` calls, two tray icons, and races on stale-cleanup and the exclusive default-sink removal.

**Decision.** Exactly one instance runs. The first process binds a unix domain socket at `$XDG_RUNTIME_DIR/autoroute.sock` and owns the sole host, `RoutingWorker`, and tray. Any later `AutoRoute` launch connects to the socket, sends a "reveal window" message, and exits without building a host. `--background` means "start hidden" (the systemd service is the canonical always-on instance); a plain launch means "start shown, or reveal the already-running instance."

**Consequences.** Only one `RoutingWorker` ever mutates the graph, eliminating reconciler races by construction. The socket doubles as the show-window IPC channel, so no DBus dependency is needed for it. Must handle a stale socket left by an unclean exit (attempt connect; on connection-refused, unlink and bind).
