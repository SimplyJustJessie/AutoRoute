# AutoRoute — install & autostart

AutoRoute is a single always-on process: an Avalonia GUI + tray with a background reconcile loop.
Exactly one instance runs at a time — launching it again just reveals the existing window
(single-instance unix socket, ADR-0005).

> These are **manual** steps. AutoRoute never installs, enables, or writes into your home on its
> own — enabling autostart is your decision.

## 0. The easy way: the in-app toggle

The ⚙ menu in the toolbar has **Start AutoRoute when I log in**. Flipping it on writes an XDG
`~/.config/autostart` entry pointing at AutoRoute's own path — the `$APPIMAGE` file when you're
running an AppImage, so there's no symlink to get wrong. Your desktop starts that entry as one of its
own session children, so it inherits the display, session DBus and tray environment a GUI app needs.
Flipping it off removes it (and cleans up any systemd unit an older build may have installed).

> Why not a systemd user service? A `systemctl --user` service starts under the systemd user manager,
> which often lacks the graphical-session environment — the tray app then can't reach a display and
> crash-loops. The desktop-session autostart above sidesteps that. The systemd unit below is still the
> right tool for a **headless** box with no desktop to autostart it.

The manual sections below are for scripted installs, headless setups, or when you want to tune the
unit yourself.

## 1. Put AutoRoute on your PATH as `AutoRoute`

Both autostart methods below launch `%h/.local/bin/AutoRoute --background`, so first make that name
exist. Pick the option that matches how you installed AutoRoute.

### Option A — from source (publish the binary)

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

### Option B — from the AppImage

The AppImage needs no publish and no .NET, and its launcher forwards arguments straight through
(`--background` and friends work). A single symlink is all autostart needs — keep the image file
wherever you like; the symlink points at it:

```bash
# Wherever you keep it — adjust the path to your downloaded image.
chmod +x ~/Apps/AutoRoute-x86_64.AppImage
mkdir -p ~/.local/bin
ln -sf ~/Apps/AutoRoute-x86_64.AppImage ~/.local/bin/AutoRoute

# Same window-free sanity check as Option A.
AutoRoute --check-host
```

> AppImages self-mount via FUSE. On a host without FUSE, install it (`fuse2` / `libfuse2`) or
> extract once (`~/Apps/AutoRoute-x86_64.AppImage --appimage-extract`) and point the symlink at the
> extracted `squashfs-root/AppRun` instead.

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
Exec=AutoRoute --background
X-GNOME-Autostart-enabled=true
```

`Exec=` resolves a bare command against `$PATH`, so this relies on `~/.local/bin` being on your PATH
(step 1). It is **not** the same as a systemd unit: `.desktop` files do not expand `~` or systemd
specifiers like `%h`, so if `~/.local/bin` isn't on your session PATH, use the absolute path
(`Exec=/home/<you>/.local/bin/AutoRoute --background`).

(systemd gives you restart-on-failure and `journalctl` integration; XDG autostart is simpler but has
neither.)
