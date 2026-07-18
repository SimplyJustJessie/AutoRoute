#!/usr/bin/env bash
# Build a self-contained linux-x64 AppImage of AutoRoute.
#
#   scripts/build-appimage.sh [output-dir]     # default output dir: out/
#
# Env:
#   VERSION       version string baked into the file name (default: git describe)
#   APPIMAGETOOL  path to appimagetool (default: found on PATH, else downloaded to out/)
#
# The publish is self-contained (runtime bundled), so the resulting AppImage has no
# .NET dependency; its glibc floor is .NET's own (~Ubuntu 22.04+), not this builder's.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/out}"
APPDIR="$OUT/AutoRoute.AppDir"
VERSION="${VERSION:-$(git -C "$ROOT" describe --tags --always 2>/dev/null || echo dev)}"

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/lib/autoroute" "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps"

dotnet publish "$ROOT/src/AutoRoute.App" -c Release -r linux-x64 --self-contained \
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
    TOOL="$OUT/appimagetool-x86_64.AppImage"
    if [ ! -x "$TOOL" ]; then
        curl -fsSL -o "$TOOL" \
            https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
        chmod +x "$TOOL"
    fi
fi

TARGET="$OUT/AutoRoute-$VERSION-x86_64.AppImage"
# --appimage-extract-and-run lets the tool run without FUSE (containers, CI runners).
if ! ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APPDIR" "$TARGET" 2>/dev/null; then
    ARCH=x86_64 "$TOOL" "$APPDIR" "$TARGET"
fi

echo "AppImage: $TARGET"
