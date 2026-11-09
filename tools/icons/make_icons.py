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


ICONS = {
    "chat": chat,
    "draft": draft,
    "history": history,
    "newchat": new_chat,
    "status": status,
    "key": key,
    "turnpath": turn_path,
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
