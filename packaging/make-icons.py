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

    print(f"written to {out}")


if __name__ == "__main__":
    main()
