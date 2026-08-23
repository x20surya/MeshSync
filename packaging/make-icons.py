#!/usr/bin/env python3
"""
Exports the Linux icon set from the Windows daemon's own icon.

`src/WinDaemon/Resources/meshsync.ico` is the icon this product already ships on Windows, and it
carries nine frames down to 16px that were sized for small rendering. Drawing the mark from its
SVG geometry instead produced correct-looking large icons and hairlines at taskbar size, where
the rings are barely over a pixel wide and effectively vanish. One source for both platforms is
also one fewer thing to keep in step.
"""
from PIL import Image
import os, sys

# What a Linux icon theme actually looks in. The .ico's 20 and 40 have nowhere to go.
WANTED = (16, 24, 32, 48, 64, 128, 256, 512)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ico_path = os.path.join(here, "..", "src", "WinDaemon", "Resources", "meshsync.ico")
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "icons")
    os.makedirs(out, exist_ok=True)

    ico = Image.open(ico_path)
    available = sorted(ico.info.get("sizes", []), key=lambda s: s[0])
    print(f"  source: {os.path.relpath(ico_path, here)} with frames {[s[0] for s in available]}")

    for px in WANTED:
        exact = next((s for s in available if s[0] == px), None)

        if exact:
            ico.size = exact
            frame = ico.convert("RGBA")
        else:
            # Only 512 lands here. Scaled from the largest frame rather than dropped, because a
            # theme that finds no large icon will upscale a small one far worse.
            ico.size = available[-1]
            frame = ico.convert("RGBA").resize((px, px), Image.LANCZOS)

        frame.save(os.path.join(out, f"meshsync-{px}.png"), "PNG")
        print(f"  meshsync-{px}.png{'' if exact else '  (scaled from %d)' % available[-1][0]}")

    write_symbolic(out)

    print(f"written to {out}")


# ──────────────────────────────────────────────────────────────────── symbolic

# The four states the panel has to tell apart, and the shape that carries each. Colour is second:
# a tray icon is recoloured by the theme and may end up white on dark, black on light, or a single
# accent - so the difference has to survive being one colour.
#
#   idle       two rings, open          paired, nothing reachable
#   active     two rings and the lens   something is connected
#   attention  the above plus a dot     a device is asking to join
#   offline    two thin rings, faded    Mesh Sync is not running
#
# Derived from the mark rather than redrawn: the brand geometry is two circles of radius 44 with
# an 11-wide stroke, centres 68 apart, on a 167x99 viewBox. At 22px that stroke lands under one
# device pixel and the lens closes up, so the proportions are recomputed per size instead of the
# artwork being scaled - which is the difference between a mark and a smudge.
SYMBOLIC_SIZES = (16, 22, 24)


def ring_geometry(px):
    """Centres, radius and stroke for one icon size, in that size's own pixels."""
    box = px
    radius = px * 0.25
    stroke = max(1.25, px * 0.095)
    gap = radius * 1.28                     # centre-to-centre, tuned so the lens stays open
    cy = box / 2
    return (box / 2 - gap / 2, box / 2 + gap / 2), cy, radius, stroke


def lens_path(left, right, cy, radius):
    """The almond where the two rings overlap - the one filled shape in the mark."""
    import math

    half = (right - left) / 2
    inner = radius * radius - half * half
    if inner <= 0:
        return None                          # the rings do not meet; there is no lens to draw

    dy = math.sqrt(inner)
    x = (left + right) / 2
    top, bottom = cy - dy, cy + dy

    return (f"M {x:.2f} {top:.2f} "
            f"A {radius:.2f} {radius:.2f} 0 0 1 {x:.2f} {bottom:.2f} "
            f"A {radius:.2f} {radius:.2f} 0 0 1 {x:.2f} {top:.2f} Z")


def symbolic_svg(px, state):
    """
    One symbolic icon, in the shape Breeze recolours.

    The stylesheet block and the ColorScheme-Text class are not decoration: they are how Plasma
    knows this icon may be repainted. Without them the tray shows it in whatever colour it was
    saved as, which is wrong on half of all colour schemes.
    """
    (left, right), cy, radius, stroke = ring_geometry(px)
    opacity = 0.45 if state == "offline" else 1.0
    width = stroke * (0.8 if state == "offline" else 1.0)

    parts = [
        f'<circle cx="{left:.2f}" cy="{cy:.2f}" r="{radius:.2f}" fill="none" '
        f'stroke="currentColor" stroke-width="{width:.2f}"/>',
        f'<circle cx="{right:.2f}" cy="{cy:.2f}" r="{radius:.2f}" fill="none" '
        f'stroke="currentColor" stroke-width="{width:.2f}"/>',
    ]

    if state in ("active", "attention"):
        path = lens_path(left, right, cy, radius)
        if path:
            parts.append(f'<path d="{path}" fill="currentColor"/>')

    badge = ""
    mask = ""
    masked = ""

    if state == "attention":
        bx, by, br = px - px * 0.19, px * 0.19, px * 0.16

        # A badge in the same colour as the rings it overlaps merges into them, and a symbolic
        # icon has no second colour to separate them with. So the rings are masked to leave a
        # gap around it - a real hole, which reads correctly whatever the theme paints it.
        mask = (f'\n    <mask id="badge-gap">'
                f'\n      <rect width="{px}" height="{px}" fill="white"/>'
                f'\n      <circle cx="{bx:.2f}" cy="{by:.2f}" r="{br * 1.42:.2f}" fill="black"/>'
                f'\n    </mask>')
        masked = ' mask="url(#badge-gap)"'
        badge = f'\n  <circle cx="{bx:.2f}" cy="{by:.2f}" r="{br:.2f}" fill="currentColor" ' \
                f'class="ColorScheme-Text" color="#232629"/>'

    body = "\n      ".join(parts)

    return f"""<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="{px}" height="{px}" viewBox="0 0 {px} {px}">
  <defs>
    <style id="current-color-scheme" type="text/css">.ColorScheme-Text {{ color: #232629; }}</style>{mask}
  </defs>
  <g class="ColorScheme-Text" color="#232629" fill="currentColor" opacity="{opacity}">
    <g{masked}>
      {body}
    </g>{badge}
  </g>
</svg>
"""


STATES = {
    "meshsync-tray-symbolic": "idle",
    "meshsync-tray-active-symbolic": "active",
    "meshsync-tray-attention-symbolic": "attention",
    "meshsync-tray-offline-symbolic": "offline",
}


def write_symbolic(out):
    """Writes the panel icons. One SVG per name; the sizes exist to be hinted, not scaled."""
    directory = os.path.join(out, "symbolic")
    os.makedirs(directory, exist_ok=True)

    for name, state in STATES.items():
        # 22 is the size a Plasma panel actually asks for; the others are for themes that pick
        # by size rather than scaling the one they find.
        for px in SYMBOLIC_SIZES:
            suffix = "" if px == 22 else f"-{px}"
            path = os.path.join(directory, f"{name}{suffix}.svg")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(symbolic_svg(px, state))

        print(f"  {name}.svg  ({', '.join(str(s) for s in SYMBOLIC_SIZES)})")


if __name__ == "__main__":
    main()
