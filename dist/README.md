# AutoRoute — install & autostart

AutoRoute is a single always-on process: an Avalonia GUI + tray with a background reconcile loop.
Exactly one instance runs at a time — launching it again just reveals the existing window
(single-instance unix socket, ADR-0005).

> These are **manual** steps. AutoRoute never installs, enables, or writes into your home on its
> own — enabling autostart is your decision.

## 1. Publish the binary

The published entry executable is `AutoRoute.App` (the assembly name; it drives Avalonia's
`avares://AutoRoute.App/...` resource URIs, so it is intentionally **not** renamed). Publish the
whole app into one directory, then expose it on your PATH as `AutoRoute` via a symlink — the .NET
apphost resolves its managed dll from its real location, so the symlink launches it correctly and
the process shows up as `AutoRoute`.

```bash
export PATH=/usr/share/dotnet:$PATH
cd /home/jessie/Files/Dev/AutoRoute

# Publish the app (framework-dependent) into a dedicated directory:
dotnet publish src/AutoRoute.App -c Release -o ~/.local/share/autoroute

# Expose it on your PATH as the name the service unit expects (%h/.local/bin/AutoRoute):
mkdir -p ~/.local/bin
ln -sf ~/.local/share/autoroute/AutoRoute.App ~/.local/bin/AutoRoute

# Sanity check (window-free): the DI graph resolves and the app exits 0.
AutoRoute --check-host
```

Run modes:

| Command | Effect |
|---|---|
| `AutoRoute` | Start (or reveal) the window; the tray + reconcile loop run alongside it. |
| `AutoRoute --background` | Start hidden — tray only. This is the autostart mode. |
| `AutoRoute --poll` | Use the polling graph monitor instead of `pw-mon` (fallback). |
| `AutoRoute --check-host` | Window-free: build + resolve the DI graph and exit (diagnostic). |
| `AutoRoute --smoke` / `--smoke-ui` | Window-free VM / headless-render smoke checks. |

## 2. Autostart via systemd (recommended)

Install the user service (copy — do not symlink into a repo you might move):

```bash
mkdir -p ~/.config/systemd/user
cp dist/systemd/autoroute.service ~/.config/systemd/user/autoroute.service
systemctl --user daemon-reload
systemctl --user enable --now autoroute.service
```

Check status and logs (AutoRoute logs one line per record, journald-friendly):

```bash
systemctl --user status autoroute.service
journalctl --user -u autoroute -f
```

Stop / disable:

```bash
systemctl --user disable --now autoroute.service
```

AutoRoute handles **SIGTERM** (what `systemctl --user stop` sends) and **SIGINT** (Ctrl-C when run
in a foreground terminal) as a graceful shutdown — the same teardown as the tray **Quit**: it stops
the reconcile worker, disposes the rule-store watcher and graph monitor, and unlinks the
`$XDG_RUNTIME_DIR/autoroute.sock` socket. So `systemctl --user stop` returns promptly instead of
waiting out `TimeoutStopSec`.

The unit orders itself `After=pipewire.service wireplumber.service` and restarts `on-failure`.
It is `PartOf=graphical-session.target` so the tray has a session bus; if your session does not pull
in `graphical-session.target`, change `WantedBy` to `default.target` only (already the `[Install]`
target) and it will still start at login.

## 3. Autostart via XDG (alternative)

If you prefer the freedesktop autostart mechanism over systemd, drop a `.desktop` launcher in
`~/.config/autostart/`:

```ini
# ~/.config/autostart/autoroute.desktop
[Desktop Entry]
Type=Application
Name=AutoRoute
Exec=%h/.local/bin/AutoRoute --background
X-GNOME-Autostart-enabled=true
```

(systemd gives you restart-on-failure and `journalctl` integration; XDG autostart is simpler but has
neither.)
