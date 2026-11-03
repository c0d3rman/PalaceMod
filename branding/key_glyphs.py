#!/usr/bin/env python3
"""Draws the key glyphs the game's own sprite sheets lack (Cmd, Backspace, F-keys, punctuation, mouse buttons 4-7).

The game's keyboard_glyphs are white rounded keycaps with the label cut out in a chunky 3x5 pixel face, tinted by TMP;
these copy that look so a mod binding on Cmd or F5 reads like a native one. Writes
src/PalaceMod/Assets/palacemod_key_glyphs.png and palacemod_key_glyphs.txt (one tab-separated "element sprite x y w h" line
per sprite: the Rewired element name, the sprite's name, its rect with y from the bottom like Unity's texture space). KeyGlyphs.cs loads both into a TMP sprite asset. Needs Pillow.

    python3 branding/key_glyphs.py
"""
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "PalaceMod" / "Assets"
SS = 4  # supersampling

# A 3x5 pixel face (some letters wider), like the game's glyph labels.
FONT = {
    "A": [".#.", "#.#", "###", "#.#", "#.#"],
    "B": ["##.", "#.#", "##.", "#.#", "##."],
    "C": [".##", "#..", "#..", "#..", ".##"],
    "D": ["##.", "#.#", "#.#", "#.#", "##."],
    "E": ["###", "#..", "##.", "#..", "###"],
    "F": ["###", "#..", "##.", "#..", "#.."],
    "G": [".##", "#..", "#.#", "#.#", ".##"],
    "H": ["#.#", "#.#", "###", "#.#", "#.#"],
    "I": ["###", ".#.", ".#.", ".#.", "###"],
    "K": ["#.#", "#.#", "##.", "#.#", "#.#"],
    "L": ["#..", "#..", "#..", "#..", "###"],
    "M": ["#...#", "##.##", "#.#.#", "#...#", "#...#"],
    "N": ["#..#", "##.#", "#.##", "#..#", "#..#"],
    "O": [".#.", "#.#", "#.#", "#.#", ".#."],
    "P": ["##.", "#.#", "##.", "#..", "#.."],
    "Q": [".#.", "#.#", "#.#", "##.", ".##"],
    "R": ["##.", "#.#", "##.", "#.#", "#.#"],
    "S": [".##", "#..", ".#.", "..#", "##."],
    "T": ["###", ".#.", ".#.", ".#.", ".#."],
    "U": ["#.#", "#.#", "#.#", "#.#", "###"],
    "W": ["#...#", "#...#", "#.#.#", "##.##", "#...#"],
    "Y": ["#.#", "#.#", ".#.", ".#.", ".#."],
    "0": ["###", "#.#", "#.#", "#.#", "###"],
    "1": [".#.", "##.", ".#.", ".#.", "###"],
    "2": ["##.", "..#", ".#.", "#..", "###"],
    "3": ["##.", "..#", ".#.", "..#", "##."],
    "4": ["#.#", "#.#", "###", "..#", "..#"],
    "5": ["###", "#..", "##.", "..#", "##."],
    "6": [".##", "#..", "###", "#.#", "###"],
    "7": ["###", "..#", ".#.", ".#.", ".#."],
    "8": ["###", "#.#", "###", "#.#", "###"],
    "9": ["###", "#.#", "###", "..#", "##."],
}

# Punctuation keys: one big symbol, drawn on a 5x7 grid.
SYMBOLS = {
    "Back Quote": ["##...", ".##..", "..#..", ".....", ".....", ".....", "....."],
    "'": [".##..", ".##..", "..#..", ".....", ".....", ".....", "....."],
    "\"": ["##.##", "##.##", ".#..#", ".....", ".....", ".....", "....."],
    ",": [".....", ".....", ".....", ".....", ".##..", ".##..", "..#.."],
    ".": [".....", ".....", ".....", ".....", ".....", ".##..", ".##.."],
    ";": [".....", ".##..", ".##..", ".....", ".##..", ".##..", "..#.."],
    ":": [".....", ".##..", ".##..", ".....", ".##..", ".##..", "....."],
    "/": ["....#", "...#.", "...#.", "..#..", ".#...", ".#...", "#...."],
    "\\": ["#....", ".#...", ".#...", "..#..", "...#.", "...#.", "....#"],
    "-": [".....", ".....", ".....", "#####", ".....", ".....", "....."],
    "=": [".....", ".....", "#####", ".....", "#####", ".....", "....."],
    "+": [".....", "..#..", "..#..", "#####", "..#..", "..#..", "....."],
    "[": [".###.", ".#...", ".#...", ".#...", ".#...", ".#...", ".###."],
    "]": [".###.", "...#.", "...#.", "...#.", "...#.", "...#.", ".###."],
    "(": ["...#.", "..#..", ".#...", ".#...", ".#...", "..#..", "...#."],
    ")": [".#...", "..#..", "...#.", "...#.", "...#.", "..#..", ".#..."],
    "<": ["...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#."],
    ">": [".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#..."],
    "*": [".....", "#.#.#", ".###.", "#####", ".###.", "#.#.#", "....."],
    "#": [".#.#.", ".#.#.", "#####", ".#.#.", "#####", ".#.#.", ".#.#."],
    "!": ["..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.."],
    "?": [".###.", "#...#", "...#.", "..#..", "..#..", ".....", "..#.."],
    "^": ["..#..", ".#.#.", "#...#", ".....", ".....", ".....", "....."],
    "_": [".....", ".....", ".....", ".....", ".....", ".....", "#####"],
}

# Keys with a text label: Rewired element name -> label. Letters and digits stay plain text, as in the game.
LABELS = {
    "Left Command": "CMD", "Backspace": None, "Delete": "DEL", "Return": "ENTER", "Keypad Enter": "ENTER",
    "ESC": "ESC", "Insert": "INS", "Home": "HOME", "End": "END", "Page Up": "PGUP", "Page Down": "PGDN",
    "Caps Lock": "CAPS", "Numlock": "NUM", "Scroll Lock": "SCRL", "Pause": "PAUSE", "Clear": "CLEAR", "Help": "HELP",
    "Print": "PRINT", "Menu": "MENU", "Left Windows": "WIN", "AltGr": "ALTGR", "Break": "BREAK", "SysReq": "SYSRQ",
    **{f"F{i}": f"F{i}" for i in range(1, 16)},
}


def text_width(label, unit, gap):
    return sum(len(FONT[c][0]) for c in label) * unit + gap * (len(label) - 1)


def bitmap_mask(size, placements):
    """A mask of pixel-font cells (placements: rows, x0, y0, unit), with the corners softened like the game's face."""
    mask = Image.new("L", size, 0)
    d = ImageDraw.Draw(mask)
    unit = 1
    for rows, x0, y0, unit in placements:
        for r, row in enumerate(rows):
            for c, cell in enumerate(row):
                if cell == "#":
                    d.rectangle([round(x0 + c * unit), round(y0 + r * unit), round(x0 + (c + 1) * unit) - 1, round(y0 + (r + 1) * unit) - 1], fill=255)
    # Blur and re-threshold: rounds outer corners and eases inner ones, without thinning the strokes.
    return mask.filter(ImageFilter.GaussianBlur(unit * 0.22)).point(lambda v: 255 if v >= 128 else 0)


def cut(img, mask):
    alpha = ImageChops.subtract(img.getchannel("A"), mask)
    img.putalpha(alpha)


def keycap(w, h):
    img = Image.new("RGBA", (w * SS, h * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, w * SS - 1, h * SS - 1], radius=13 * SS, fill=(255, 255, 255, 255))
    return img, d


def label_cap(label):
    # Like the game's caps: 96x72, the label in a band ~58 px wide (wider for longer labels) and 26 px tall at most,
    # strokes one pixel-font cell (4-5 px) thick.
    cols = sum(len(FONT[c][0]) for c in label)
    band = {1: 40, 2: 50, 3: 58, 4: 66}.get(len(label), 74)
    unit = max(3.0, min(5.2, band / (cols + 0.9 * (len(label) - 1))))
    gap = unit * 0.9
    w = 96
    h = 72 if unit >= 4.4 else 64
    img, _ = keycap(w, h)
    u, g = unit * SS, gap * SS
    x = (w * SS - text_width(label, u, g)) / 2
    y = (h * SS - 5 * u) / 2
    placements = []
    for ch in label:
        placements.append((FONT[ch], x, y, u))
        x += len(FONT[ch][0]) * u + g
    cut(img, bitmap_mask(img.size, placements))
    return img


def symbol_cap(rows):
    w = h = 72
    img, _ = keycap(w, h)
    u = 5.6 * SS
    cut(img, bitmap_mask(img.size, [(rows, (w * SS - 5 * u) / 2, (h * SS - 7 * u) / 2, u)]))
    return img


def command_cap():
    # The Mac command key's looped square, which reads faster than "CMD".
    w, h = 96, 72
    img, _ = keycap(w, h)
    mask = Image.new("L", img.size, 0)
    d = ImageDraw.Draw(mask)
    s, t, r = 11.5 * SS, 4.6 * SS, 5.6 * SS  # loop centres' offset, stroke, loop radius
    cx, cy = w * SS / 2, h * SS / 2
    inner = s - r  # the square's sides, tangent to the loops
    for sx in (-1, 1):
        for sy in (-1, 1):
            ox, oy = cx + sx * s, cy + sy * s
            d.ellipse([ox - r - t / 2, oy - r - t / 2, ox + r + t / 2, oy + r + t / 2], outline=255, width=round(t))
    for sx in (-1, 1):
        x = cx + sx * inner
        d.rectangle([x - t / 2, cy - s, x + t / 2, cy + s], fill=255)
        y = cy + sx * inner
        d.rectangle([cx - s, y - t / 2, cx + s, y + t / 2], fill=255)
    cut(img, mask)
    return img


def backspace_cap():
    # A left-pointing tag with an x, the usual backspace mark.
    img, d = keycap(96, 72)
    s = SS
    left, right, top, bottom, mid = 22 * s, 74 * s, 22 * s, 50 * s, 36 * s
    d.polygon([(left, mid), (left + 14 * s, top), (right, top), (right, bottom), (left + 14 * s, bottom)], fill=(0, 0, 0, 0))
    cx, cy, r = 53 * s, mid, 7 * s
    d.line([(cx - r, cy - r), (cx + r, cy + r)], fill=(255, 255, 255, 255), width=5 * s)
    d.line([(cx - r, cy + r), (cx + r, cy - r)], fill=(255, 255, 255, 255), width=5 * s)
    return img


def mouse_glyph(number):
    # The game's mouse outline (90x90), with the side button's number in the body.
    w = h = 90
    img = Image.new("RGBA", (w * SS, h * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = SS
    white = (255, 255, 255, 255)
    stroke = 6 * s
    body = [12 * s, 4 * s, 78 * s, 86 * s]
    d.rounded_rectangle(body, radius=30 * s, fill=white)
    d.rounded_rectangle([body[0] + stroke, body[1] + stroke, body[2] - stroke, body[3] - stroke], radius=24 * s, fill=(0, 0, 0, 0))
    d.rectangle([body[0], 36 * s, body[2], 36 * s + stroke - 1], fill=white)  # the buttons' edge
    d.rectangle([42 * s, body[1], 48 * s - 1, 36 * s], fill=white)  # split between left and right
    rows = FONT[str(number)]
    u = 6 * s
    img2 = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d2 = ImageDraw.Draw(img2)
    x0, y0 = (w * s - 3 * u) // 2, 47 * s
    for r, row in enumerate(rows):
        for c, cell in enumerate(row):
            if cell == "#":
                d2.rectangle([x0 + c * u, y0 + r * u, x0 + (c + 1) * u - 1, y0 + (r + 1) * u - 1], fill=white)
    img.alpha_composite(img2)
    return img


def main():
    sprites = {}
    for name, label in LABELS.items():
        sprites[name] = backspace_cap() if name == "Backspace" else command_cap() if name == "Left Command" else label_cap(label)
    for name, rows in SYMBOLS.items():
        sprites[name] = symbol_cap(rows)
    for n in range(4, 8):
        sprites[f"Mouse Button {n}"] = mouse_glyph(n)

    # Shelf-pack into a 1024-wide sheet with 4 px padding.
    pad, sheet_w = 4, 1024
    small = {k: v.resize((v.width // SS, v.height // SS), Image.LANCZOS) for k, v in sprites.items()}
    x = y = pad
    shelf = 0
    placed = {}
    for name, img in small.items():
        if x + img.width + pad > sheet_w:
            x, y, shelf = pad, y + shelf + pad, 0
        placed[name] = (x, y)
        x += img.width + pad
        shelf = max(shelf, img.height)
    sheet_h = y + shelf + pad
    sheet = Image.new("RGBA", (sheet_w, sheet_h), (255, 255, 255, 0))
    lines = []
    for name, img in small.items():
        px, py = placed[name]
        # Keep transparent pixels white so tinting and filtering don't darken the edges.
        rgba = Image.new("RGBA", img.size, (255, 255, 255, 0))
        rgba.putalpha(img.getchannel("A"))
        sheet.paste(rgba, (px, py))
        # Rich-text tags can't quote every character, so punctuation sprites get a code-point name.
        sprite = name if all(c.isalnum() or c == " " for c in name) else "U+" + "".join(f"{ord(c):04X}" for c in name)
        lines.append(f"{name}\t{sprite}\t{px}\t{sheet_h - py - img.height}\t{img.width}\t{img.height}")
    sheet.save(OUT / "palacemod_key_glyphs.png", optimize=True)
    (OUT / "palacemod_key_glyphs.txt").write_text("\n".join(lines) + "\n")
    print(f"{len(lines)} glyphs, {sheet_w}x{sheet_h}")


if __name__ == "__main__":
    main()
