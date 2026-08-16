# Handoff: Mesh Sync — brand assets

## Overview
Identity set for **Mesh Sync**, a private, local-first utility that keeps a laptop's and a
phone's clipboard in sync over the user's own Wi-Fi. This bundle contains the mark, the
wordmark, the app icons, the splash lockup, and three spot illustrations for the setup
wizard, plus the exact geometry needed to rebuild any of them.

Tone the assets are built to: trustworthy, quiet, precise. Flat vector only — no gradients,
no bevels, no shadows, no 3D, no glass. Warm off-white surfaces, one accent colour,
generous negative space.

## About the design files
The `reference/*.dc.html` files are **design references authored in HTML** — they show
intended geometry and proportion, not production code to paste in. `svg/` and `png/` are
the real deliverables. Implement by dropping the SVGs into the target app (React Native,
SwiftUI, Kotlin, web — whatever the codebase already uses) and wiring the PNGs into the
platform icon pipelines. Nothing here needs a runtime.

## Fidelity
**High-fidelity.** Colours, geometry, and type specs are final. Reproduce exactly; the
SVGs are the source of truth and every number below is already baked into them.

---

## Assets

| File | Size | Background | Use |
|---|---|---|---|
| `svg/mesh-sync-mark.svg` | 167 × 99 | transparent | the mark, any size |
| `svg/mesh-sync-mark-reversed.svg` | 167 × 99 | transparent | mark on deep teal |
| `svg/mesh-sync-icon-1024.svg` · `png/mesh-sync-icon-1024.png` | 1024 × 1024 | `#F7F6F3` | iOS / macOS / store icon |
| `svg/mesh-sync-icon-adaptive-1024.svg` · `png/mesh-sync-icon-adaptive-1024.png` | 1024 × 1024 | transparent | Android adaptive foreground |
| `svg/mesh-sync-lockup-1024.svg` · `png/mesh-sync-lockup-1024.png` | 1024 × 1024 | transparent | splash / launch |
| `svg/mesh-sync-illo-pair.svg` · `png/mesh-sync-illo-pair-512.png` | 512 × 512 | transparent | wizard step A — scan to pair |
| `svg/mesh-sync-illo-copy.svg` · `png/mesh-sync-illo-copy-512.png` | 512 × 512 | transparent | wizard step B — copy detected |
| `svg/mesh-sync-illo-send.svg` · `png/mesh-sync-illo-send-512.png` | 512 × 512 | transparent | wizard step C — image sent |

---

## The mark

Two rings of equal radius, overlapped so they hold one shape in common: two devices, one
clipboard. The lens is the only filled form in the identity — do not fill anything else.

Drawn in a coordinate space where the two ring centres sit on `y = 120`:

```
circle  cx 86   cy 120  r 44   stroke #2F7A6B  stroke-width 11  fill none
circle  cx 154  cy 120  r 44   stroke #2F7A6B  stroke-width 11  fill none
path    M 120 92.1 A 44 44 0 0 1 120 147.9 A 44 44 0 0 1 120 92.1 Z   fill #2F7A6B
viewBox 36.5 70.5 167 99      (tight to the outer stroke edges)
```

- Aspect ratio **167 : 99** (1.687). Never distort; scale uniformly.
- Ring centres are **68 apart**; the resulting lens is **20 wide × 55.8 tall**.
- Stroke is geometric, not scaled separately — export at any size by scaling the whole SVG.
- **Clear space:** one ring radius (44 units = 26.3% of mark width) on all four sides.
- **Minimum size:** 24 px wide. Below that the lens closes up.
- **Reversed:** strokes and lens become `#F7F6F3` on `#2F7A6B`. No other colour pairs.

## The wordmark

```
font-family    Archivo (Google Fonts), weight 500
case           uppercase only
letter-spacing 0.15em
colour         #262523  (or #F7F6F3 reversed)
```

Never bold, italic, condensed, or lowercase. Minimum rendered width 96 px.

CSS note: `letter-spacing` adds trailing space after the final `C`, so a centred wordmark
sits ~0.075em left of true centre. The lockup compensates with `text-indent: .15em` (HTML)
and `x = 505.7` instead of `512` (SVG). Keep that compensation when you re-typeset it.

`svg/mesh-sync-lockup-1024.svg` references the Archivo family by name. If the target
platform can't guarantee the font, either convert the text to outlines or use the PNG.

## App icon

- Canvas 1024 × 1024, background `#F7F6F3`, **square** — no rounded corners, no inner
  padding beyond what's specified; the OS applies its own mask.
- Mark is **62% of canvas width** (635 px), optically centred:
  `translate(55.71, 55.73) scale(3.8024)` applied to the mark's native coordinates.

**Android adaptive:** same mark on a transparent 1024 canvas at **600 px wide** (58.6%),
which keeps every pixel inside the centre-60% safe zone (`translate(80.86, 80.91)
scale(3.5928)`). Pair it with a solid `#F7F6F3` background layer.

## Splash lockup

Stacked, centred, transparent, 1024 × 1024. The block occupies ~49% of canvas height.

```
mark        600 wide  (355.7 tall), top edge y = 260
gap         88
wordmark    Archivo 500, 84 px, letter-spacing .15em, baseline y = 770
```

Mark and wordmark come out to nearly the same width (600 vs ~612) — that match is the
point of the lockup; if you re-set the type at another size, re-match the widths.

## Setup illustrations

Three 512 × 512 transparent SVGs, one visual system:

```
stroke        #2F7A6B, width 10   (9 for interior detail, 8 for the phone speaker)
caps + joins  round
corner radii  12 (small) · 14 (QR) · 16 (cards) · 18 (phone)
screen fills  #E6F1EE
text fills    #77726A  (the three lines in "copy detected")
sand #F0E3D0  used exactly once, on the image card in "image sent"
```

Shared props: phone `96 × 168 rx 18` at `x 36, y 172`; laptop screen `132 × 94 rx 12` at
`x 328, y 226` over a solid teal base bar `156 × 12 rx 6` at `x 316, y 328`, with an 8-unit
hinge gap. Both devices sit on a common baseline at `y = 340`.

- **A · scan to pair** — phone, 96 × 96 QR (three finder patterns + three data modules),
  laptop. Left to right, evenly spaced.
- **B · copy detected** — two offset cards (back outlined, front pale-teal filled with three
  warm-grey text lines) and two concentric arcs radiating from the front card's top-right
  corner. Whole group nudged `translate(-11, 15)` to centre optically.
- **C · image sent** — phone, two motion dashes, a sand image card (teal dot + peak),
  laptop. Direction of travel is left to right.

No text, no human figures, no perspective. If more illustrations are needed later, build
them from the same primitives at the same weights.

---

## Design tokens

```
--warm-off-white  #F7F6F3   primary background
--white           #FFFFFF   raised surfaces
--deep-teal       #2F7A6B   the single accent / brand colour
--pale-teal       #E6F1EE   soft fills
--warm-sand       #F0E3D0   rare secondary accent, ≤ once per screen
--near-black      #262523   text
--warm-grey       #77726A   secondary text

type/brand        Archivo 500 (wordmark), Archivo 400 (supporting text)
type/spec         Space Mono 400 — documentation and spec labels only
stroke/mark       11 @ 167-wide coordinate space
stroke/illo       10 @ 512 canvas
radius            12 · 14 · 16 · 18 (illustration family)
```

Rules that carry into product UI: teal never sits on teal; sand appears at most once per
screen; nothing gets a shadow, gradient, or blur.

## Assets provenance
All artwork is original vector built for this project — no third-party icon sets, no
photography, no licensed illustration. Archivo and Space Mono are Google Fonts (OFL).

## Files
```
svg/         8 vector sources — the deliverable
png/         6 raster exports at final pixel sizes
reference/   HTML design references
             Mesh Sync Style Sheet.dc.html  one-page identity sheet
             Mesh Sync Assets.dc.html       every asset at true output size
             Mesh Sync Directions.dc.html   the four explored directions (1a–1d)
```
