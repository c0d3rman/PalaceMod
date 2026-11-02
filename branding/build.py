#!/usr/bin/env python3
"""Generates PalaceMod's logo SVGs and their PNG exports.

    python3 branding/build.py

Needs `rsvg-convert` (brew install librsvg) and fontTools (pip install fonttools). Text is outlined into the SVGs, so
they render identically everywhere; the fonts (OFL, from Google Fonts) are downloaded once to ~/.cache/palacemod-branding.
Outputs go to branding/svg/ and branding/png/, and the in-game textures to src/PalaceMod/Assets/.
"""
import math
import os
import subprocess
import urllib.request

from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
SVG_DIR = os.path.join(HERE, "svg")
PNG_DIR = os.path.join(HERE, "png")
GAME_ASSETS = os.path.join(REPO, "src", "PalaceMod", "Assets")
CACHE = os.path.expanduser("~/.cache/palacemod-branding")

# ---- Palette (keep in sync with branding/README.md and ModStyle.cs) ----------------------------------------------------
CHARCOAL = "#1A1C1D"    # the game's panel colour; dark background
GOLD = "#C8A674"        # the game's text gold
GOLD_LIT = "#D8BB8A"    # lit face of the stone
GOLD_SHADE = "#AE8F60"  # shaded face of the stone
DIM = "#7D6E57"         # the game's dim text
PAPER = "#F3EDE2"       # light background
INK = "#4A3D2C"         # text on light backgrounds
GEM = ("#A6E3D3", "#6BC7B0", "#4FA893", "#3A8676")  # verdigris gem facets: lit, accent (signature), shade, deep
TEAL_WORLD = ("#173F3E", "#1D4E4C", "#236766", "#2B7570")  # the game's grass tones, for backdrops

FONTS = {
    "blackletter": ("UnifrakturCook-Bold.ttf", "unifrakturcook/UnifrakturCook-Bold.ttf", None),
    "serif-bold": ("CormorantGaramond-700.ttf", "cormorantgaramond/CormorantGaramond%5Bwght%5D.ttf", 700),
    "serif-italic": ("CormorantGaramond-Italic-400.ttf", "cormorantgaramond/CormorantGaramond-Italic%5Bwght%5D.ttf", 400),
}


def font_path(key):
    name, remote, weight = FONTS[key]
    path = os.path.join(CACHE, name)
    if not os.path.exists(path):
        os.makedirs(CACHE, exist_ok=True)
        raw = os.path.join(CACHE, "raw-" + os.path.basename(remote).replace("%5B", "[").replace("%5D", "]"))
        if not os.path.exists(raw):
            urllib.request.urlretrieve("https://raw.githubusercontent.com/google/fonts/main/ofl/" + remote, raw)
        if weight is None:
            os.replace(raw, path)
        else:
            from fontTools.varLib.instancer import instantiateVariableFont
            instantiateVariableFont(TTFont(raw), {"wght": weight}).save(path)
    return path


_fonts = {}


def text(key, s, size, x, baseline, tracking=0.0, fill=GOLD):
    """Outlined text as an SVG <path>. Returns (svg, width, bounds). tracking is in ems."""
    f = _fonts.setdefault(key, TTFont(font_path(key)))
    upm, cmap, glyphs, hmtx = f["head"].unitsPerEm, f.getBestCmap(), f.getGlyphSet(), f["hmtx"]
    scale = size / upm
    pen, bounds = SVGPathPen(glyphs), BoundsPen(glyphs)
    advance = 0.0
    names = [cmap[ord(c)] for c in s]
    for i, name in enumerate(names):
        t = (scale, 0, 0, -scale, x + advance * scale, baseline)
        glyphs[name].draw(TransformPen(pen, t))
        glyphs[name].draw(TransformPen(bounds, t))
        advance += hmtx[name][0] + (tracking * upm if i < len(names) - 1 else 0)
    return f'<path d="{pen.getCommands()}" fill="{fill}"/>', advance * scale, bounds.bounds


def measure(key, s, size, tracking=0.0):
    return text(key, s, size, 0, 0, tracking)[1]


# ---- The mark: a palace gateway with the verdigris gem inside -------------------------------------------------------

def arch(x0, x1, spring, bottom, ratio):
    """A pointed arch from `bottom` up to the springline, its two arcs of radius ratio*span meeting at the apex."""
    span = x1 - x0
    r = span * ratio
    apex = spring - math.sqrt(r * r - (r - span / 2) ** 2)
    return (f"M{x0},{bottom} V{spring} A{r:.2f},{r:.2f} 0 0 1 {(x0 + x1) / 2},{apex:.2f} "
            f"A{r:.2f},{r:.2f} 0 0 1 {x1},{spring} V{bottom} Z")


def gem(cx, cy, w, h, colors=None):
    """The emblem: a four-facet gem lit from the top left, like the game's flat-shaded low-poly models."""
    lit, accent, shade, deep = colors or GEM
    left, right, top, bottom = cx - w / 2, cx + w / 2, cy - h / 2, cy + h / 2
    return (f'<path d="M{cx},{top} L{cx},{cy} L{left},{cy} Z" fill="{lit}"/>'
            f'<path d="M{cx},{top} L{right},{cy} L{cx},{cy} Z" fill="{accent}"/>'
            f'<path d="M{left},{cy} L{cx},{cy} L{cx},{bottom} Z" fill="{shade}"/>'
            f'<path d="M{right},{cy} L{cx},{bottom} L{cx},{cy} Z" fill="{deep}"/>')


def mark(uid, small=False):
    """The mark on a 256x256 canvas. `small` is the 16-32 px cut: heavier stone, bigger gem, no plinth overhang."""
    if small:
        outer, inner = arch(36, 220, 136, 228, 0.72), arch(92, 164, 136, 228, 0.72)
        plinth, gem_svg = "M28,228 H228 V252 H28 Z", gem(128, 178, 64, 92)
    else:
        outer, inner = arch(44, 212, 128, 222, 0.72), arch(88, 168, 128, 222, 0.72)
        plinth, gem_svg = "M28,222 H228 V242 H28 Z", gem(128, 172, 52, 76)
    stone = f"{outer} {inner} {plinth}"
    return (f'<clipPath id="{uid}L"><rect width="128" height="256"/></clipPath>'
            f'<clipPath id="{uid}R"><rect x="128" width="128" height="256"/></clipPath>'
            f'<path d="{inner}" fill="{CHARCOAL}"/>'
            f'<path d="{stone}" fill="{GOLD_LIT}" fill-rule="evenodd" clip-path="url(#{uid}L)"/>'
            f'<path d="{stone}" fill="{GOLD_SHADE}" fill-rule="evenodd" clip-path="url(#{uid}R)"/>'
            + gem_svg)


def place(body, x, y, size):
    """Places a 256-unit drawing at (x, y) scaled to `size`."""
    return f'<g transform="translate({x:.2f},{y:.2f}) scale({size / 256:.5f})">{body}</g>'


def doc(w, h, body, bg=None):
    rect = f'<rect width="{w}" height="{h}" fill="{bg}"/>' if bg else ""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}">'
            f'{rect}{body}</svg>\n')


# ---- Wordmark: "Palace" in the game's blackletter, "MOD" in its tracked small caps ----------------------------------

def wordmark(x, baseline, cap, ink):
    """Returns (svg, width). `cap` is the blackletter size; the caps are set at 0.42x with wide tracking."""
    palace, pw, _ = text("blackletter", "Palace", cap, x, baseline, 0.06, ink)
    gap = cap * 0.16
    mod, mw, _ = text("serif-bold", "MOD", cap * 0.40, x + pw + gap, baseline, 0.32, ink)
    return palace + mod, pw + gap + mw


def lockup(ink, h=256):
    """Horizontal lockup: mark, then wordmark. Returns (svg, width, height) at height h."""
    cap = h * 0.62
    words, ww = wordmark(h * 1.08, h * 0.80, cap, ink)
    return place(mark("lk"), 0, 0, h) + words, h * 1.08 + ww + h * 28 / 256, h


def low_poly_hills(w, h, top):
    """A band of flat-shaded triangles along the bottom, in the game's grass teals."""
    import random
    rnd = random.Random(11)
    cols = 11
    step = w / cols
    rows = [top, top + (h - top) * 0.45, h + 1]
    grid = []
    for r, y in enumerate(rows):
        row = []
        for c in range(cols + 1):
            x = c * step + (rnd.uniform(-0.3, 0.3) * step if 0 < c < cols else 0)
            jitter = (h - top) * (0.22 if r == 0 else 0.1 if r == 1 else 0)
            row.append((x, y + rnd.uniform(-jitter, jitter)))
        grid.append(row)

    def tri(a, b, c, base):
        # Flat shading: faces whose upper edge rises to the right face the light (top left), so they're lighter.
        (x0, y0), (x1, y1) = sorted([a, b, c], key=lambda p: p[1])[:2]
        slope = (y0 - y1) / (x0 - x1 + 1e-6)
        shade = max(0, min(len(TEAL_WORLD) - 1, base + (1 if slope < -0.05 else -1 if slope > 0.05 else 0)))
        pts = " ".join(f"{x:.1f},{y:.1f}" for x, y in (a, b, c))
        return f'<polygon points="{pts}" fill="{TEAL_WORLD[shade]}" stroke="{TEAL_WORLD[shade]}" stroke-width="0.6"/>'

    parts = []
    for r in range(2):
        base = 2 if r == 0 else 1
        for c in range(cols):
            a, b = grid[r][c], grid[r][c + 1]
            d, e = grid[r + 1][c], grid[r + 1][c + 1]
            if (r + c) % 2:
                parts += [tri(a, b, d, base), tri(b, e, d, base - 1)]
            else:
                parts += [tri(a, b, e, base), tri(a, e, d, base - 1)]
    # A few pines on the ridge, like the game's cone trees.
    for c in (1, 2, 8, 9, 10):
        (x0, y0), (x1, y1) = grid[0][c], grid[0][c + 1]
        for k in range(rnd.choice((3, 4))):
            t = rnd.uniform(0.15, 0.85)
            x, y = x0 + (x1 - x0) * t, y0 + (y1 - y0) * t + 6
            th, tw = rnd.uniform(40, 64), rnd.uniform(11, 15)
            parts.append(f'<polygon points="{x - tw:.1f},{y:.1f} {x:.1f},{y - th:.1f} {x:.1f},{y:.1f}" fill="#7DBE5C"/>'
                         f'<polygon points="{x:.1f},{y:.1f} {x:.1f},{y - th:.1f} {x + tw:.1f},{y:.1f}" fill="#4E9550"/>')
    return "".join(parts)


# ---- Outputs --------------------------------------------------------------------------------------------------------

def write(name, svg):
    path = os.path.join(SVG_DIR, name)
    with open(path, "w") as f:
        f.write(svg)
    return path


def png(svg_path, out, w=None, h=None):
    args = ["rsvg-convert", svg_path, "-o", out]
    if w:
        args += ["-w", str(w)]
    if h:
        args += ["-h", str(h)]
    subprocess.run(args, check=True)


def main():
    os.makedirs(SVG_DIR, exist_ok=True)
    os.makedirs(PNG_DIR, exist_ok=True)
    out = lambda name: os.path.join(PNG_DIR, name)

    # Mark, and the small cut for favicons and in-game use at 16-32 px.
    p = write("mark.svg", doc(256, 256, mark("m")))
    png(p, out("mark-512.png"), 512, 512)
    p = write("mark-small.svg", doc(256, 256, mark("s", small=True)))
    for s in (16, 24, 32):
        png(p, out(f"mark-{s}.png"), s, s)

    # Emblem: the gem alone, the mod's signature on its UI.
    p = write("emblem.svg", doc(256, 256, gem(128, 128, 176, 256)))
    png(p, out("emblem-64.png"), 64, 64)
    png(p, os.path.join(GAME_ASSETS, "palacemod_emblem.png"), 128, 128)
    # The same gem in greys, for tinting in-game (e.g. a notification's kind colour multiplies it).
    p = write("emblem-tintable.svg", doc(256, 256, gem(128, 128, 176, 256, ("#FFFFFF", "#D6D6D6", "#ADADAD", "#8C8C8C"))))
    png(p, os.path.join(GAME_ASSETS, "palacemod_emblem_tint.png"), 128, 128)

    # Thunderstore icon: exactly 256x256, the mark on the game's charcoal.
    p = write("icon.svg", doc(256, 256, place(mark("i"), 24, 20, 208), CHARCOAL))
    png(p, out("icon-256.png"), 256, 256)

    # Horizontal lockups for dark and light backgrounds.
    for name, ink, bg in (("dark", GOLD, None), ("light", INK, None)):
        body, w, h = lockup(ink)
        p = write(f"logo-{name}.svg", doc(math.ceil(w), h, body, bg))
        png(p, out(f"logo-{name}.png"), h=256)

    # GitHub social preview, 1280x640.
    W, H = 1280, 640
    body, lw, lh = lockup(GOLD, 190)
    x = (W - lw) / 2
    tag = "Quality of life, previews and tools for Thronefall"
    tw = measure("serif-italic", tag, 40)
    tag_svg, _, _ = text("serif-italic", tag, 40, (W - tw) / 2, 405, 0, DIM)
    social = (f'<g transform="translate({x:.1f},140)">{body}</g>' + tag_svg + low_poly_hills(W, H, 530))
    p = write("social-preview.svg", doc(W, H, social, CHARCOAL))
    png(p, out("social-preview-1280x640.png"), W, H)

    # Main-menu badge texture: the small-cut mark, so it stays crisp at ~28 px.
    png(os.path.join(SVG_DIR, "mark-small.svg"), os.path.join(GAME_ASSETS, "palacemod_mark.png"), 96, 96)
    print("wrote", SVG_DIR, PNG_DIR)


if __name__ == "__main__":
    main()
