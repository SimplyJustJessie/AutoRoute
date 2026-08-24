#!/usr/bin/env bash
# Build a self-contained linux-x64 AppImage of AutoRoute.
#
#   scripts/build-appimage.sh [output-dir]     # default output dir: out/
#
# Env:
#   AUTOROUTE_VERSION  version string baked into the file name (default: git describe).
#                      Deliberately NOT named "VERSION": MSBuild maps environment variables
#                      onto build properties, so an exported VERSION=v0.1.0 would override
#                      $(Version) and break dotnet publish ("not a valid version string").
#   APPIMAGETOOL       path to appimagetool (default: found on PATH, else downloaded to out/)
#
# The publish is self-contained (runtime bundled), so the resulting AppImage has no
# .NET dependency; its glibc floor is .NET's own (~Ubuntu 22.04+), not this builder's.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/out}"
APPDIR="$OUT/AutoRoute.AppDir"
VERSION="${AUTOROUTE_VERSION:-$(git -C "$ROOT" describe --tags --always 2>/dev/null || echo dev)}"

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/lib/autoroute" "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps"

# Bake the version into the assembly so the running app knows what it is (the in-app updater
# compares it against the latest Gitea release tag). InformationalVersion carries the exact string;
# $(Version) needs a strict numeric X.Y.Z, so only pass it when VERSION is a clean vX.Y.Z tag —
# a `git describe` fallback like "v0.3.0-4-gabc123" or "dev" is left to default.
VERSION_PROPS=(-p:InformationalVersion="${VERSION#v}")
if [[ "${VERSION#v}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    VERSION_PROPS+=(-p:Version="${VERSION#v}")
fi

# ReadyToRun: precompiles native code for linux-x64 instead of JIT-ing everything cold on first
# run. Startup latency for a self-contained (untrimmed, ~460-DLL) publish mounted via AppImage's
# FUSE layer is dominated by that first-call JIT cost, not disk I/O — R2R is the standard fix and
# purely additive (falls back to JIT for anything not covered, no behavior change). PublishTrimmed
# is NOT enabled here: ViewLocator.cs resolves views by reflection (Type.GetType on a string-built
# name) and is explicitly marked [RequiresUnreferencedCode] — trimming it needs its own care pass
# (root it, or replace the reflection lookup) rather than riding along with this change.
# Locked restore: the released binary is built from exactly the package set committed in
# packages.lock.json (version-pinned, SHA-512 verified) or the build fails — same guarantee the
# AUR package makes, so both shipping artifacts come from one audited dependency graph.
dotnet restore "$ROOT/src/AutoRoute.App" -r linux-x64 --locked-mode

dotnet publish "$ROOT/src/AutoRoute.App" -c Release -r linux-x64 --self-contained --no-restore \
    -p:PublishReadyToRun=true \
    "${VERSION_PROPS[@]}" \
    -o "$APPDIR/usr/lib/autoroute"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/autoroute/AutoRoute.App" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# Matches the .desktop Exec= name (also the /usr/bin name the AUR package installs).
ln -sf ../lib/autoroute/AutoRoute.App "$APPDIR/usr/bin/autoroute"

cp "$ROOT/packaging/autoroute.desktop" "$APPDIR/autoroute.desktop"
cp "$ROOT/packaging/autoroute.desktop" "$APPDIR/usr/share/applications/autoroute.desktop"
cp "$ROOT/packaging/autoroute.png" "$APPDIR/autoroute.png"
cp "$ROOT/packaging/autoroute.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/autoroute.png"

TOOL="${APPIMAGETOOL:-}"
if [ -z "$TOOL" ] && command -v appimagetool >/dev/null 2>&1; then TOOL=appimagetool; fi
if [ -z "$TOOL" ]; then
    # Kept out of $OUT's top level so out/*.AppImage globs (CI artifact upload, release
    # attachment) only ever match the built AutoRoute image, not the tool itself.
    mkdir -p "$OUT/.tools"
    TOOL="$OUT/.tools/appimagetool-x86_64.AppImage"
    if [ ! -x "$TOOL" ]; then
        curl -fsSL -o "$TOOL" \
            https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
        # Optional integrity pin: export APPIMAGETOOL_SHA256=<hash> to fail on a tampered or
        # unexpected download ("continuous" is a moving tag, so no hash is hard-coded here).
        if [ -n "${APPIMAGETOOL_SHA256:-}" ]; then
            echo "$APPIMAGETOOL_SHA256  $TOOL" | sha256sum -c - || { rm -f "$TOOL"; exit 1; }
        fi
        chmod +x "$TOOL"
    fi
fi

TARGET="$OUT/AutoRoute-$VERSION-x86_64.AppImage"
# --appimage-extract-and-run lets the tool run without FUSE (containers, CI runners).
if ! ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APPDIR" "$TARGET" 2>/dev/null; then
    ARCH=x86_64 "$TOOL" "$APPDIR" "$TARGET"
fi

echo "AppImage: $TARGET"
