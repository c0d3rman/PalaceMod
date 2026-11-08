"""The Builder Studio's tool and top-bar icons: white line silhouettes, drawn at 8x and downsampled to 128x128.
Run with Pillow: python3 branding/studio/draw_icons.py src/PalaceMod/Assets (writes studio_<name>.png)."""
import math, sys
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(sys.argv[1]); OUT.mkdir(parents=True, exist_ok=True)
S = 1024
W = (255, 255, 255, 255)
C = (0, 0, 0, 0)
LW = 64  # line width


def canvas():
    im = Image.new("RGBA", (S, S), C)
    return im, ImageDraw.Draw(im)


def save(im, name):
    bbox = im.getbbox()
    content = im.crop(bbox)
    m = int(S * 0.08)
    scale = min((S - 2 * m) / content.width, (S - 2 * m) / content.height)
    content = content.resize((max(1, int(content.width * scale)), max(1, int(content.height * scale))), Image.LANCZOS)
    full = Image.new("RGBA", (S, S), C)
    full.alpha_composite(content, ((S - content.width) // 2, (S - content.height) // 2))
    full.resize((128, 128), Image.LANCZOS).save(OUT / f"studio_{name}.png")


def arrowhead(d, tip, angle, size=170):
    a1, a2 = angle + math.radians(150), angle - math.radians(150)
    p1 = (tip[0] + size * math.cos(a1), tip[1] + size * math.sin(a1))
    p2 = (tip[0] + size * math.cos(a2), tip[1] + size * math.sin(a2))
    d.polygon([tip, p1, p2], fill=W)


def select():
    im, d = canvas()
    d.polygon([(260, 120), (260, 820), (430, 660), (560, 930), (680, 870), (550, 610), (770, 600)], fill=W)
    save(im, "select")


def marquee():
    im, d = canvas()
    x0, y0, x1, y1 = 120, 220, 900, 800
    dash = 110
    for x in range(x0, x1, dash * 2):
        d.rectangle((x, y0, min(x + dash, x1), y0 + LW), fill=W)
        d.rectangle((x, y1 - LW, min(x + dash, x1), y1), fill=W)
    for y in range(y0, y1, dash * 2):
        d.rectangle((x0, y, x0 + LW, min(y + dash, y1)), fill=W)
        d.rectangle((x1 - LW, y, x1, min(y + dash, y1)), fill=W)
    save(im, "marquee")


def lasso():
    im, d = canvas()
    d.ellipse((140, 140, 900, 640), outline=W, width=LW)
    d.line([(300, 580), (260, 760), (380, 900)], fill=W, width=LW, joint="curve")
    d.ellipse((330, 850, 450, 970), fill=W)
    save(im, "lasso")


def paint():
    im, d = canvas()
    d.ellipse((120, 360, 620, 860), outline=W, width=LW)
    d.line([(500, 520), (880, 140)], fill=W, width=LW * 2)
    d.polygon([(430, 600), (520, 690), (590, 620), (500, 530)], fill=W)
    save(im, "paint")


def move():
    im, d = canvas()
    c = S // 2
    d.line([(c, 160), (c, 864)], fill=W, width=LW)
    d.line([(160, c), (864, c)], fill=W, width=LW)
    for ang, tip in [(-math.pi / 2, (c, 100)), (math.pi / 2, (c, 924)), (math.pi, (100, c)), (0, (924, c))]:
        arrowhead(d, tip, ang)
    save(im, "move")


def rotate():
    im, d = canvas()
    d.arc((170, 170, 854, 854), 200, 470, fill=W, width=LW)
    a = math.radians(200)
    tip = (512 + 342 * math.cos(a), 512 + 342 * math.sin(a))
    arrowhead(d, (tip[0] + 10, tip[1] - 60), math.radians(-80), 200)
    save(im, "rotate")


def place():
    im, d = canvas()
    d.rectangle((452, 160, 572, 864), fill=W)
    d.rectangle((160, 452, 864, 572), fill=W)
    save(im, "place")


def erase():
    im, d = canvas()
    d.polygon([(160, 620), (560, 220), (880, 540), (480, 940)], outline=W, width=LW)
    d.polygon([(160, 620), (360, 420), (680, 740), (480, 940)], fill=W)
    save(im, "erase")


def menu():
    im, d = canvas()
    for y in (240, 482, 724):
        d.rounded_rectangle((140, y, 884, y + 90), 40, fill=W)
    save(im, "menu")


def undo():
    im, d = canvas()
    d.arc((260, 300, 900, 900), 180, 380, fill=W, width=LW)
    arrowhead(d, (230, 560), math.radians(250), 220)
    save(im, "undo")


def redo():
    im, d = canvas()
    im2 = Image.new("RGBA", (S, S), C)
    d2 = ImageDraw.Draw(im2)
    d2.arc((260, 300, 900, 900), 180, 380, fill=W, width=LW)
    arrowhead(d2, (230, 560), math.radians(250), 220)
    save(im2.transpose(Image.FLIP_LEFT_RIGHT), "redo")


def play():
    im, d = canvas()
    d.polygon([(260, 140), (260, 884), (860, 512)], fill=W)
    save(im, "play")


def warning():
    im, d = canvas()
    d.polygon([(512, 100), (940, 900), (84, 900)], fill=W)
    d.rectangle((470, 360, 554, 680), fill=C)
    d.ellipse((462, 740, 562, 840), fill=C)
    save(im, "warning")


for f in (select, marquee, lasso, paint, move, rotate, place, erase, menu, undo, redo, play, warning):
    f()
