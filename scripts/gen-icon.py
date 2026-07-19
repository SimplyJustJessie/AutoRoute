#!/usr/bin/env python3
"""Regenerate the app icon from the in-app logo mark (BoardView toolbar, Border.logoMark):
a 26x26 rounded tile filled with the diagonal LogoBrush gradient (#8FA8FF -> #6D5EF0) holding
the white 14x14 IconWave glyph. Writes the window/tray icon asset and the packaging icon.

    python3 scripts/gen-icon.py

Requires Pillow (pip install pillow).
"""
from PIL import Image, ImageDraw

SIZE = 256
# Border.logoMark proportions: CornerRadius 7 on a 26px tile; 14px icon centered.
RADIUS = round(SIZE * 7 / 26)
GLYPH_BOX = SIZE * 14 / 26

# StreamGeometry IconWave (App.axaml): five bars on a 16-unit grid, tight bounds 2..14.5 x 2..14.
BARS = [
    (2.00, 6.00, 3.50, 10.00),
    (4.75, 3.50, 6.25, 12.50),
    (7.50, 5.25, 9.00, 10.75),
    (10.25, 2.00, 11.75, 14.00),
    (13.00, 6.00, 14.50, 10.00),
]
BOUNDS = (2.0, 2.0, 14.5, 14.0)

START, END = (0x8F, 0xA8, 0xFF), (0x6D, 0x5E, 0xF0)  # LogoBrush 0%,0% -> 100%,100%


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def main() -> None:
    # Diagonal gradient: t = normalized projection onto the TL->BR axis.
    img = Image.new("RGBA", (SIZE, SIZE))
    px = img.load()
    for y in range(SIZE):
        for x in range(SIZE):
            t = (x + y) / (2 * (SIZE - 1))
            px[x, y] = (*lerp(START, END, t), 255)

    # Rounded-corner alpha mask (supersampled for smooth edges).
    ss = 4
    mask = Image.new("L", (SIZE * ss, SIZE * ss), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, SIZE * ss - 1, SIZE * ss - 1), radius=RADIUS * ss, fill=255)
    img.putalpha(mask.resize((SIZE, SIZE), Image.LANCZOS))

    # White wave bars, Uniform-scaled into a centered GLYPH_BOX (like the 14x14 Path).
    bx0, by0, bx1, by1 = BOUNDS
    scale = GLYPH_BOX / max(bx1 - bx0, by1 - by0)
    ox = (SIZE - (bx1 - bx0) * scale) / 2
    oy = (SIZE - (by1 - by0) * scale) / 2
    glyph = Image.new("L", (SIZE * ss, SIZE * ss), 0)
    gd = ImageDraw.Draw(glyph)
    for x0, y0, x1, y1 in BARS:
        gd.rectangle((
            (ox + (x0 - bx0) * scale) * ss, (oy + (y0 - by0) * scale) * ss,
            (ox + (x1 - bx0) * scale) * ss - 1, (oy + (y1 - by0) * scale) * ss - 1), fill=255)
    white = Image.new("RGBA", (SIZE, SIZE), (0xF2, 0xF4, 0xFF, 255))  # toolbar glyph fill #F2F4FF
    img.paste(white, (0, 0), glyph.resize((SIZE, SIZE), Image.LANCZOS))

    for out in ("src/AutoRoute.App/Assets/tray-icon.png", "packaging/autoroute.png"):
        img.save(out)
        print("wrote", out)


if __name__ == "__main__":
    main()
