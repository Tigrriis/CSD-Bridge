"""Generate the ribbon icons in src/CsdBridge.BricsCAD/Resources.

Run from the repo root:  python tools/icons/make_icons.py   (needs Pillow)

Each icon is drawn at 8x on a transparent canvas and downsampled, giving
smooth 16 px (small button) and 32 px (large button) PNGs. Colours are
mid-tones with dark outlines so they read on BricsCAD's light and dark themes.
"""

from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parents[2] / "src" / "CsdBridge.BricsCAD" / "Resources"
S = 256  # drawing canvas; 8x the large size

BLUE = (59, 130, 246, 255)
BLUE_DARK = (29, 78, 160, 255)
ORANGE = (224, 122, 79, 255)
ORANGE_DARK = (150, 70, 40, 255)
GREEN = (34, 160, 90, 255)
GREEN_DARK = (16, 100, 55, 255)
GOLD = (230, 175, 50, 255)
GOLD_DARK = (140, 100, 20, 255)
WHITE = (255, 255, 255, 255)
GREY = (90, 100, 115, 255)
OUTLINE = 10


def canvas():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    return img, ImageDraw.Draw(img)


def bubble(d, box, fill, outline):
    x0, y0, x1, y1 = box
    d.rounded_rectangle(box, radius=40, fill=fill, outline=outline, width=OUTLINE)
    # tail at the bottom left
    tail = [(x0 + 40, y1 - 6), (x0 + 30, y1 + 46), (x0 + 95, y1 - 6)]
    d.polygon(tail, fill=fill, outline=outline)
    d.line([tail[0], tail[1], tail[2]], fill=outline, width=OUTLINE, joint="curve")
    # cover the outline where the tail joins the bubble
    d.line([(x0 + 45, y1 - 6), (x0 + 90, y1 - 6)], fill=fill, width=OUTLINE + 2)


def chat():
    img, d = canvas()
    bubble(d, (24, 34, 232, 182), BLUE, BLUE_DARK)
    for cx in (80, 128, 176):
        d.ellipse((cx - 15, 93, cx + 15, 123), fill=WHITE)
    return img


def draft():
    img, d = canvas()
    # a drawn line under the pencil
    d.line([(30, 226), (150, 226)], fill=GREY, width=14)
    # pencil body: a rotated rectangle from bottom-left to top-right
    body = [(58, 166), (170, 54), (210, 94), (98, 206)]
    d.polygon(body, fill=ORANGE, outline=ORANGE_DARK)
    d.line(body + [body[0]], fill=ORANGE_DARK, width=OUTLINE, joint="curve")
    # eraser end
    cap = [(170, 54), (190, 34), (230, 74), (210, 94)]
    d.polygon(cap, fill=GREY)
    d.line(cap + [cap[0]], fill=ORANGE_DARK, width=OUTLINE, joint="curve")
    # sharpened tip
    tip = [(58, 166), (98, 206), (36, 228)]
    d.polygon(tip, fill=(245, 215, 170, 255))
    d.line(tip + [tip[0]], fill=ORANGE_DARK, width=OUTLINE, joint="curve")
    d.polygon([(36, 228), (47, 196), (68, 217)], fill=ORANGE_DARK)
    return img


def history():
    img, d = canvas()
    d.ellipse((22, 22, 234, 234), fill=BLUE, outline=BLUE_DARK, width=OUTLINE)
    d.ellipse((52, 52, 204, 204), fill=WHITE)
    d.line([(128, 128), (128, 74)], fill=BLUE_DARK, width=18)
    d.line([(128, 128), (168, 150)], fill=BLUE_DARK, width=18)
    d.ellipse((116, 116, 140, 140), fill=BLUE_DARK)
    return img


def new_chat():
    img, d = canvas()
    bubble(d, (16, 34, 200, 170), BLUE, BLUE_DARK)
    d.ellipse((136, 120, 244, 228), fill=GREEN, outline=GREEN_DARK, width=OUTLINE)
    d.line([(190, 146), (190, 202)], fill=WHITE, width=18)
    d.line([(162, 174), (218, 174)], fill=WHITE, width=18)
    return img


def status():
    img, d = canvas()
    d.ellipse((22, 22, 234, 234), fill=BLUE, outline=BLUE_DARK, width=OUTLINE)
    d.ellipse((112, 58, 144, 90), fill=WHITE)
    d.rounded_rectangle((112, 108, 144, 196), radius=10, fill=WHITE)
    return img


def key():
    img, d = canvas()
    d.ellipse((20, 70, 136, 186), fill=GOLD, outline=GOLD_DARK, width=OUTLINE)
    d.ellipse((52, 102, 88, 138), fill=(0, 0, 0, 0), outline=GOLD_DARK, width=OUTLINE)
    d.rectangle((126, 112, 236, 144), fill=GOLD, outline=GOLD_DARK, width=OUTLINE)
    d.rectangle((182, 140, 204, 182), fill=GOLD, outline=GOLD_DARK, width=OUTLINE)
    d.rectangle((212, 140, 234, 172), fill=GOLD, outline=GOLD_DARK, width=OUTLINE)
    # tidy the joins between shaft and teeth
    d.rectangle((136, 122, 226, 136), fill=GOLD)
    # tilt so the key fills the square and stays legible at 16 px
    return img.rotate(35, resample=Image.BICUBIC, center=(128, 128)).resize((S, S))


def scale_bar():
    img, d = canvas()
    # alternating segments, like a drawing's scale bar, with ticks above
    x0, x1, y0, y1 = 20, 236, 110, 168
    seg = (x1 - x0) / 4
    for i in range(4):
        a = x0 + i * seg
        d.rectangle((a, y0, a + seg, y1), fill=BLUE if i % 2 == 0 else WHITE)
    d.rectangle((x0, y0, x1, y1), outline=BLUE_DARK, width=OUTLINE)
    for i in range(5):
        x = x0 + i * seg
        d.line([(x, y0 - (44 if i % 2 == 0 else 26)), (x, y0)], fill=BLUE_DARK, width=OUTLINE)
    return img


def routines():
    img, d = canvas()
    # a page of numbered steps...
    d.rounded_rectangle((26, 16, 186, 238), radius=22, fill=WHITE, outline=BLUE_DARK, width=OUTLINE)
    for y, end in ((66, 160), (114, 160), (162, 118)):
        d.ellipse((50, y - 13, 76, y + 13), fill=BLUE)
        d.line([(92, y), (end, y)], fill=GREY, width=14)
    # ...and a run button
    d.ellipse((132, 132, 246, 246), fill=GREEN, outline=GREEN_DARK, width=OUTLINE)
    d.polygon([(174, 160), (174, 218), (222, 189)], fill=WHITE)
    return img


def turn_path():
    """A car in plan at the bottom left, with its swept path curving away to the right."""
    img, d = canvas()
    RED = (205, 55, 45, 255)
    # swept path: a band between two concentric quarter arcs about (236, 236)
    d.pieslice((236 - 206, 236 - 206, 236 + 206, 236 + 206), 180, 270, fill=RED)
    d.pieslice((236 - 118, 236 - 118, 236 + 118, 236 + 118), 180, 270, fill=(0, 0, 0, 0))
    # dashed clearance line outside the band
    for a in range(182, 270, 16):
        d.arc((236 - 236, 236 - 236, 236 + 236, 236 + 236), a, a + 8, fill=RED, width=10)
    # the car, parked at the start, pointing up
    d.rounded_rectangle((52, 118, 128, 238), radius=18, fill=BLUE, outline=BLUE_DARK, width=OUTLINE)
    for (x0, y0) in ((44, 132), (118, 132), (44, 196), (118, 196)):
        d.rectangle((x0, y0, x0 + 18, y0 + 30), fill=BLUE_DARK)
    return img


def turn_edit():
    """The turn path icon with a pencil over it."""
    img = turn_path()
    d = ImageDraw.Draw(img)
    body = [(150, 196), (214, 132), (238, 156), (174, 220)]
    d.polygon(body, fill=ORANGE, outline=ORANGE_DARK)
    d.line(body + [body[0]], fill=ORANGE_DARK, width=OUTLINE, joint="curve")
    tip = [(150, 196), (174, 220), (136, 234)]
    d.polygon(tip, fill=(245, 215, 170, 255))
    d.line(tip + [tip[0]], fill=ORANGE_DARK, width=OUTLINE, joint="curve")
    return img


def turn_solve():
    """A car turning round between two kerbs in three movements: forward, back, forward."""
    img, d = canvas()
    RED = (205, 55, 45, 255)
    # the kerbs
    d.line([(8, 22), (248, 22)], fill=GREY, width=18)
    d.line([(8, 234), (248, 234)], fill=GREY, width=18)
    # the three movements, the last with an arrow head
    path = [(70, 176), (112, 70), (156, 190), (204, 86)]
    d.line(path, fill=RED, width=20, joint="curve")
    for (x, y) in path[:-1]:
        d.ellipse((x - 10, y - 10, x + 10, y + 10), fill=RED)
    d.polygon([(222, 44), (176, 84), (232, 104)], fill=RED)
    # the car at the start
    d.rounded_rectangle((14, 160, 84, 210), radius=16, fill=BLUE, outline=BLUE_DARK, width=OUTLINE)
    return img


def turn_in_out():
    """A site with a driveway in its boundary: the path comes in forwards, stops, and goes out forwards."""
    img, d = canvas()
    RED = (205, 55, 45, 255)
    # the site boundary, with the driveway gap
    d.line([(8, 78), (52, 78)], fill=GREY, width=18)
    d.line([(204, 78), (248, 78)], fill=GREY, width=18)
    # in down the left, round the site, out up the right
    path = [(88, 14), (88, 150), (106, 196), (128, 206), (150, 196), (168, 150), (168, 64)]
    d.line(path, fill=RED, width=20, joint="curve")
    d.polygon([(168, 10), (136, 66), (200, 66)], fill=RED)
    # the car stopped on the site
    d.rounded_rectangle((94, 186, 164, 236), radius=16, fill=BLUE, outline=BLUE_DARK, width=OUTLINE)
    return img


def clearance():
    """A car in side view clearing a hump in the ground."""
    img, d = canvas()
    GROUND = (120, 90, 60, 255)
    # ground with a hump
    d.polygon([(8, 236), (8, 200), (70, 200), (128, 150), (186, 200), (248, 200), (248, 236)], fill=GROUND)
    # chassis line and body
    d.rounded_rectangle((30, 90, 226, 152), radius=20, fill=BLUE, outline=BLUE_DARK, width=OUTLINE)
    d.rounded_rectangle((78, 50, 190, 100), radius=18, fill=BLUE, outline=BLUE_DARK, width=OUTLINE)
    d.rectangle((78, 90, 190, 100), fill=BLUE)
    # wheels on the ground either side of the hump
    for cx in (62, 194):
        d.ellipse((cx - 26, 148, cx + 26, 200), fill=GREY, outline=BLUE_DARK, width=OUTLINE)
    # the clearance under the body at the crest, in red
    d.line([(128, 152), (128, 150)], fill=(205, 55, 45, 255), width=6)
    d.polygon([(110, 170), (146, 170), (128, 152)], fill=(205, 55, 45, 255))
    return img


def pipe_hatch():
    img, d = canvas()
    # a long section: ground line above a hatched pipe falling to the left
    d.line([(20, 96), (90, 70), (160, 58), (236, 30)], fill=GREY, width=14, joint="curve")
    pipe = [(20, 196), (236, 120), (236, 176), (20, 252 - 6)]
    d.polygon(pipe, fill=GREEN, outline=GREEN_DARK)
    d.line(pipe + [pipe[0]], fill=GREEN_DARK, width=OUTLINE, joint="curve")
    # pit walls at each end
    d.line([(20, 96), (20, 248)], fill=GREY, width=14)
    d.line([(236, 30), (236, 178)], fill=GREY, width=14)
    return img


def vp_layers():
    img, d = canvas()
    # a sheet with a grid of dots: filled = shown, hollow = frozen in that layout's viewports
    d.rounded_rectangle((16, 16, 240, 240), radius=24, fill=WHITE, outline=GREY, width=OUTLINE)
    d.line([(16, 80), (240, 80)], fill=GREY, width=8)
    d.line([(80, 16), (80, 240)], fill=GREY, width=8)
    states = [
        [True, False, True],
        [True, True, False],
        [False, True, True],
    ]
    for row, cells in enumerate(states):
        for col, shown in enumerate(cells):
            cx, cy = 112 + col * 52, 112 + row * 52
            box = (cx - 19, cy - 19, cx + 19, cy + 19)
            if shown:
                d.ellipse(box, fill=GREEN, outline=GREEN_DARK, width=6)
            else:
                d.ellipse(box, fill=WHITE, outline=GREY, width=8)
    # layer bars in the name column
    for row in range(3):
        y = 112 + row * 52
        d.line([(34, y), (62, y)], fill=BLUE, width=14)
    return img


ICONS = {
    "chat": chat,
    "draft": draft,
    "history": history,
    "routines": routines,
    "newchat": new_chat,
    "status": status,
    "key": key,
    "scalebar": scale_bar,
    "turnpath": turn_path,
    "turnedit": turn_edit,
    "turnsolve": turn_solve,
    "turninout": turn_in_out,
    "clearance": clearance,
    "pipehatch": pipe_hatch,
    "vplayers": vp_layers,
}


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, draw in ICONS.items():
        big = draw()
        for size in (16, 32):
            big.resize((size, size), Image.LANCZOS).save(OUT / f"{name}_{size}.png")
    print(f"wrote {len(ICONS) * 2} icons to {OUT}")


if __name__ == "__main__":
    main()
