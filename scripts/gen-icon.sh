#!/usr/bin/env bash
# Generate the AutoRoute app icon from the in-app brand mark, so the taskbar/tray/desktop icon
# always matches the toolbar logo. The source of truth is src/AutoRoute.App/App.axaml:
#   - LogoBrush   : diagonal gradient #8FA8FF -> #6D5EF0
#   - IconWave    : the soundwave glyph, drawn in #F2F4FF
# Requires rsvg-convert (librsvg). Re-run after changing the brand colours or glyph.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SIZE="${1:-256}"

# The IconWave path (16x16 viewBox), verbatim from App.axaml minus Avalonia's "F1 " fill-rule prefix.
WAVE='M8.5 2a.5.5 0 0 1 .5.5v11a.5.5 0 0 1-1 0v-11a.5.5 0 0 1 .5-.5m-2 2a.5.5 0 0 1 .5.5v7a.5.5 0 0 1-1 0v-7a.5.5 0 0 1 .5-.5m4 0a.5.5 0 0 1 .5.5v7a.5.5 0 0 1-1 0v-7a.5.5 0 0 1 .5-.5m-6 1.5A.5.5 0 0 1 5 6v4a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5m8 0a.5.5 0 0 1 .5.5v4a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5m-10 1A.5.5 0 0 1 3 7v2a.5.5 0 0 1-1 0V7a.5.5 0 0 1 .5-.5m12 0a.5.5 0 0 1 .5.5v2a.5.5 0 0 1-1 0V7a.5.5 0 0 1 .5-.5'

# Rounded-square badge (corner ratio matches the toolbar logoMark, 7/26) with the wave centred at
# ~52% — the 16-unit glyph scaled x8 (=128px) sits in the middle of the 256 canvas, nudged down 2px
# so the wave's visual mass is centred.
SVG="$(mktemp --suffix=.svg)"
cat > "$SVG" <<EOF
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#8FA8FF"/>
      <stop offset="1" stop-color="#6D5EF0"/>
    </linearGradient>
  </defs>
  <rect width="256" height="256" rx="69" ry="69" fill="url(#bg)"/>
  <g transform="translate(64,66) scale(8)" fill="#F2F4FF">
    <path d="$WAVE"/>
  </g>
</svg>
EOF

TRAY="$ROOT/src/AutoRoute.App/Assets/tray-icon.png"
DESKTOP="$ROOT/packaging/autoroute.png"
rsvg-convert -w "$SIZE" -h "$SIZE" "$SVG" -o "$TRAY"
cp "$TRAY" "$DESKTOP"
rm -f "$SVG"

echo "Wrote $TRAY and $DESKTOP (${SIZE}x${SIZE})"
