"""The economy planner's own icons (buildings the game has no sprite for, and the lock): white silhouettes, drawn at 8x
and downsampled to 128x128. Run with Pillow: python3 branding/planner/draw_icons.py <out dir>, then copy each
<kind>.png to src/PalaceMod/Assets/planner_<kind>.png."""
import math, sys
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(sys.argv[1]); OUT.mkdir(parents=True, exist_ok=True)
S = 1024           # working canvas
W = (255, 255, 255, 255)
C = (0, 0, 0, 0)   # clear (punch holes)


def canvas():
    im = Image.new("RGBA", (S, S), C)
    return im, ImageDraw.Draw(im)


def save(im, name):
    bbox = im.getbbox()
    # centre the content with a small margin, keep aspect
    content = im.crop(bbox)
    m = int(S * 0.04)
    scale = min((S - 2 * m) / content.width, (S - 2 * m) / content.height)
    content = content.resize((max(1, int(content.width * scale)), max(1, int(content.height * scale))), Image.LANCZOS)
    full = Image.new("RGBA", (S, S), C)
    full.alpha_composite(content, ((S - content.width) // 2, (S - content.height) // 2))
    full.resize((128, 128), Image.LANCZOS).save(OUT / f"{name}.png")


def rr(d, box, r, fill=W):
    d.rounded_rectangle(box, r, fill=fill)


# --- wall: crenellated curtain wall with brick courses ---------------------------------------
def wall():
    im, d = canvas()
    x0, x1, top, bot = 80, 944, 330, 860
    merlon_h = 130
    d.rectangle((x0, top, x1, bot), fill=W)
    n = 4
    mw = (x1 - x0) / (2 * n - 1)
    for i in range(n):
        a = x0 + i * 2 * mw
        d.rectangle((a, top - merlon_h, a + mw, top + 5), fill=W)
    # brick joints punched out
    rows = 4
    rh = (bot - top) / rows
    for r in range(1, rows):
        y = top + r * rh
        d.rectangle((x0, y - 11, x1, y + 11), fill=C)
    for r in range(rows):
        y0, y1 = top + r * rh, top + (r + 1) * rh
        off = 0 if r % 2 == 0 else 0.5
        k = 0
        while True:
            x = x0 + (k + off) * 216 + 108 * (r % 2)
            if x >= x1 - 40:
                break
            if x > x0 + 40:
                d.rectangle((x - 11, y0 + 6, x + 11, y1 - 6), fill=C)
            k += 1
    save(im, "wall")


# --- gate: crenellated gatehouse with arched opening and portcullis --------------------------
def gate():
    im, d = canvas()
    x0, x1, top, bot = 110, 914, 300, 900
    d.rectangle((x0, top, x1, bot), fill=W)
    n = 4
    mw = (x1 - x0) / (2 * n - 1)
    for i in range(n):
        a = x0 + i * 2 * mw
        d.rectangle((a, top - 130, a + mw, top + 5), fill=W)
    # arch opening
    ax0, ax1, ay = 320, 704, 590
    r = (ax1 - ax0) / 2
    d.rectangle((ax0, ay, ax1, bot + 10), fill=C)
    d.ellipse((ax0, ay - r, ax1, ay + r), fill=C)
    # portcullis bars inside the opening (lower part of the arch)
    pt = ay - r + 70
    bars = 4
    for i in range(bars):
        x = ax0 + (i + 1) * (ax1 - ax0) / (bars + 1)
        d.rectangle((x - 22, pt, x + 22, bot - 50), fill=W)
    for y in (pt + 150, pt + 320):
        d.rectangle((ax0, y - 20, ax1, y + 20), fill=W)
    # re-clip bars to the arch shape
    mask = Image.new("L", (S, S), 0)
    md = ImageDraw.Draw(mask)
    md.rectangle((ax0, ay, ax1, bot - 50), fill=255)
    md.ellipse((ax0, ay - r, ax1, ay + r), fill=255)
    grid = im.copy()
    # rebuild: wall without opening, opening area replaced with the grid only
    base, bd = canvas()
    bd.rectangle((x0, top, x1, bot), fill=W)
    for i in range(n):
        a = x0 + i * 2 * mw
        bd.rectangle((a, top - 130, a + mw, top + 5), fill=W)
    # outline of the arch (frame) stays solid; inside comes from grid
    inner = Image.new("L", (S, S), 0)
    ImageDraw.Draw(inner).rectangle((ax0, ay, ax1, bot + 10), fill=255)
    ImageDraw.Draw(inner).ellipse((ax0, ay - r, ax1, ay + r), fill=255)
    base.paste(Image.new("RGBA", (S, S), C), (0, 0), inner)
    gm = Image.new("L", (S, S), 0)
    gm.paste(mask, (0, 0))
    base.paste(grid, (0, 0), Image.composite(mask, Image.new("L", (S, S), 0), mask))
    save(base, "gate")


# --- bridge: stone arch bridge with a parapet ----------------------------------------------
def bridge():
    im, d = canvas()
    x0, x1 = 40, 984
    pts = []
    for i in range(41):
        t = i / 40
        pts.append((x0 + t * (x1 - x0), 420 - 70 * math.sin(math.pi * t)))
    d.polygon(pts + [(x1, 800), (x0, 800)], fill=W)
    # parapet band with openings
    par = [(x, y - 120) for x, y in pts]
    d.polygon(par + list(reversed(pts)), fill=W)
    for i in range(1, 6):
        t = i / 6
        x = x0 + t * (x1 - x0)
        y = 420 - 70 * math.sin(math.pi * t)
        d.rectangle((x - 45, y - 80, x + 45, y - 30), fill=C)
    # main arch cut-out
    cx, r = 512, 270
    d.ellipse((cx - r, 650 - r, cx + r, 650 + r), fill=C)
    d.rectangle((cx - r, 650, cx + r, 810), fill=C)
    # water
    for i, y in enumerate((880, 960)):
        wp = [(x, y + 20 * math.sin(x / 50 + i * 2)) for x in range(cx - 300 + i * 80, cx + 300 - i * 80, 8)]
        d.line(wp, fill=W, width=40, joint="curve")
    save(im, "bridge")


# --- barricades: crossed sharpened stakes on a beam -----------------------------------------
def barricades():
    im, d = canvas()

    def stake(x, angle):
        L, w = 820, 110
        a = math.radians(angle)
        dx, dy = math.sin(a), -math.cos(a)
        px, py = math.cos(a), math.sin(a)
        cx, cy = x, 700
        b0 = (cx - dx * L * 0.45, cy - dy * L * 0.45)
        tip = (cx + dx * L * 0.55, cy + dy * L * 0.55)
        se = (cx + dx * (L * 0.55 - 150), cy + dy * (L * 0.55 - 150))
        d.polygon([(b0[0] + px * w / 2, b0[1] + py * w / 2), (se[0] + px * w / 2, se[1] + py * w / 2), tip,
                   (se[0] - px * w / 2, se[1] - py * w / 2), (b0[0] - px * w / 2, b0[1] - py * w / 2)], fill=W)
    for x in (300, 724):
        stake(x, -38)
        stake(x, 38)
    d.rectangle((60, 615, 964, 650), fill=C)
    d.rounded_rectangle((60, 650, 964, 760), 40, fill=W)
    d.rectangle((60, 760, 964, 790), fill=C)
    save(im, "barricades")


# --- windmill (mill) ------------------------------------------------------------------------
def mill():
    im, d = canvas()
    # tower (tapered) with cap
    d.polygon([(400, 470), (624, 470), (680, 960), (344, 960)], fill=W)
    d.polygon([(370, 480), (512, 360), (654, 480)], fill=W)
    # door
    d.rectangle((470, 820, 554, 960), fill=C)
    d.ellipse((470, 778, 554, 862), fill=C)
    # sails: 4 blades around hub
    hub = (512, 450)
    for k in range(4):
        a = math.radians(45 + 90 * k)
        ux, uy = math.cos(a), math.sin(a)
        vx, vy = -uy, ux
        L, w0, w1 = 430, 30, 120
        # spar
        d.line((hub, (hub[0] + ux * L, hub[1] + uy * L)), fill=W, width=40)
        # lattice sail on one side of the spar
        p0 = (hub[0] + ux * 120, hub[1] + uy * 120)
        p1 = (hub[0] + ux * L, hub[1] + uy * L)
        poly = [p0, p1, (p1[0] + vx * w1, p1[1] + vy * w1), (p0[0] + vx * w1, p0[1] + vy * w1)]
        d.polygon(poly, fill=W)
        # lattice holes
        for i in range(3):
            t0 = 0.15 + i * 0.28
            t1 = t0 + 0.2
            q0 = (p0[0] + (p1[0] - p0[0]) * t0, p0[1] + (p1[1] - p0[1]) * t0)
            q1 = (p0[0] + (p1[0] - p0[0]) * t1, p0[1] + (p1[1] - p0[1]) * t1)
            inset = 28
            hole = [(q0[0] + vx * inset, q0[1] + vy * inset), (q1[0] + vx * inset, q1[1] + vy * inset),
                    (q1[0] + vx * (w1 - inset), q1[1] + vy * (w1 - inset)), (q0[0] + vx * (w1 - inset), q0[1] + vy * (w1 - inset))]
            d.polygon(hole, fill=C)
    d.ellipse((hub[0] - 55, hub[1] - 55, hub[0] + 55, hub[1] + 55), fill=W)
    d.ellipse((hub[0] - 22, hub[1] - 22, hub[0] + 22, hub[1] + 22), fill=C)
    save(im, "mill")


# --- anvil (blacksmith) and royal forge (anvil + crown) --------------------------------------
def anvil_shape(d, oy=0):
    d.polygon([(60, 470 + oy), (300, 470 + oy), (300, 590 + oy), (220, 590 + oy)], fill=W)      # horn
    d.rectangle((280, 470 + oy, 920, 590 + oy), fill=W)                                       # face
    d.polygon([(330, 590 + oy), (880, 590 + oy), (720, 700 + oy), (480, 700 + oy)], fill=W)    # waist
    d.rectangle((480, 690 + oy, 720, 790 + oy), fill=W)
    d.polygon([(340, 900 + oy), (480, 780 + oy), (720, 780 + oy), (860, 900 + oy)], fill=W)    # foot
    d.rectangle((320, 890 + oy, 880, 950 + oy), fill=W)


def blacksmith():
    im, d = canvas()
    anvil_shape(d)
    # hammer: head resting above the face, handle up to the right
    a = math.radians(-40)
    ux, uy = math.cos(a), math.sin(a)      # along the handle
    vx, vy = -uy, ux                        # along the head
    hx, hy = 560, 340                       # head centre
    end = (hx + ux * 420, hy + uy * 420)
    d.line(((hx, hy), end), fill=W, width=64)
    d.ellipse((end[0] - 32, end[1] - 32, end[0] + 32, end[1] + 32), fill=W)
    hw, hh = 190, 120
    d.polygon([(hx - vx * hw - ux * hh / 2, hy - vy * hw - uy * hh / 2), (hx + vx * hw - ux * hh / 2, hy + vy * hw - uy * hh / 2),
               (hx + vx * hw + ux * hh / 2, hy + vy * hw + uy * hh / 2), (hx - vx * hw + ux * hh / 2, hy - vy * hw + uy * hh / 2)], fill=W)
    save(im, "blacksmith")


def royal_forge():
    im, d = canvas()
    anvil_shape(d, oy=0)
    base_y, top_y = 410, 150
    x0, x1 = 330, 790
    d.polygon([(x0, base_y), (x0, top_y + 60), (x0 + 115, top_y + 150), (512, top_y), (x1 - 115, top_y + 150),
               (x1, top_y + 60), (x1, base_y)], fill=W)
    for x, y in ((x0, top_y + 40), (512, top_y - 25), (x1, top_y + 40)):
        d.ellipse((x - 42, y - 42, x + 42, y + 42), fill=W)
    save(im, "royal_forge")


# --- temple: classical pediment and columns -------------------------------------------------
def temple():
    im, d = canvas()
    d.polygon([(80, 360), (512, 110), (944, 360)], fill=W)         # pediment
    d.polygon([(250, 320), (512, 180), (774, 320)], fill=C)        # tympanum hole
    d.polygon([(330, 300), (512, 205), (694, 300)], fill=W)        # inner triangle
    d.rectangle((100, 380, 924, 450), fill=W)                      # entablature
    for i in range(5):
        x = 170 + i * 171
        d.rectangle((x - 45, 480, x + 45, 800), fill=W)
        d.rectangle((x - 62, 465, x + 62, 500), fill=W)
        d.rectangle((x - 62, 790, x + 62, 825), fill=W)
    d.rectangle((70, 850, 954, 900), fill=W)
    d.rectangle((40, 920, 984, 970), fill=W)
    save(im, "temple")


# --- summoning circle: ring with pentagram --------------------------------------------------
def summoning_circle():
    im, d = canvas()
    c, R = 512, 470
    d.ellipse((c - R, c - R, c + R, c + R), fill=W)
    d.ellipse((c - R + 60, c - R + 60, c + R - 60, c + R - 60), fill=C)
    r = R - 75
    pts = [(c + r * math.sin(math.radians(144 * k)), c - r * math.cos(math.radians(144 * k))) for k in range(6)]
    for a, b in zip(pts, pts[1:]):
        d.line((a, b), fill=W, width=56)
    for p in pts[:5]:
        d.ellipse((p[0] - 28, p[1] - 28, p[0] + 28, p[1] + 28), fill=W)
    save(im, "summoning_circle")


# --- lock: shackle over a block with a keyhole (markers of slots that can't be built yet) ------------------
def lock():
    im, d = canvas()
    d.arc((300, 80, 724, 560), 180, 360, fill=W, width=100)
    d.rectangle((300, 320, 400, 470), fill=W)
    d.rectangle((624, 320, 724, 470), fill=W)
    rr(d, (200, 430, 824, 940), 90)
    d.ellipse((452, 560, 572, 680), fill=C)
    d.polygon([(480, 640), (544, 640), (566, 820), (458, 820)], fill=C)
    save(im, "lock")


for f in (wall, gate, bridge, barricades, mill, blacksmith, royal_forge, temple, summoning_circle, lock):
    f()
print("ok")
