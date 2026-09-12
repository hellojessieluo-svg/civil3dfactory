# -*- coding: utf-8 -*-
"""Generate app.ico: a rounded printer with a corner badge. Run: python make_icon.py (needs Pillow)."""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
SIZE = 512
BLUE = (47, 123, 245, 255)
BLUE_DARK = (29, 86, 179, 255)
WHITE = (255, 255, 255, 255)
PAPER = (240, 244, 248, 255)
BADGE = (255, 122, 24, 255)


def _font(size: int) -> ImageFont.FreeTypeFont:
    for name in ("arialbd.ttf", "segoeuib.ttf", "msyhbd.ttc", "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def draw_icon(size: int = SIZE) -> Image.Image:
    s = size / 512
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Rounded square background
    d.rounded_rectangle((28 * s, 28 * s, 484 * s, 484 * s), radius=110 * s, fill=BLUE)

    # Sheet of paper sticking out of the top of the printer
    d.rounded_rectangle((150 * s, 92 * s, 362 * s, 230 * s), radius=22 * s, fill=PAPER)
    for i, y in enumerate((130, 160, 190)):
        w = 140 if i != 2 else 90
        d.rounded_rectangle((186 * s, y * s, (186 + w) * s, (y + 12) * s), radius=6 * s, fill=(170, 184, 200, 255))

    # Printer body
    d.rounded_rectangle((92 * s, 210 * s, 420 * s, 380 * s), radius=48 * s, fill=WHITE)
    # Paper feed slot on the body
    d.rounded_rectangle((150 * s, 210 * s, 362 * s, 232 * s), radius=8 * s, fill=(206, 216, 228, 255))
    # Status light
    d.ellipse((350 * s, 262 * s, 384 * s, 296 * s), fill=(72, 199, 120, 255))
    # Output tray at the bottom
    d.rounded_rectangle((150 * s, 340 * s, 362 * s, 430 * s), radius=22 * s, fill=PAPER)
    d.rounded_rectangle((186 * s, 386 * s, 300 * s, 398 * s), radius=6 * s, fill=(170, 184, 200, 255))

    # Corner badge
    r = 96 * s
    cx, cy = 400 * s, 400 * s
    d.ellipse((cx - r - 12 * s, cy - r - 12 * s, cx + r + 12 * s, cy + r + 12 * s), fill=BLUE)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=BADGE)
    font = _font(int(104 * s))
    text = "3D"
    bbox = d.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    d.text((cx - tw / 2 - bbox[0], cy - th / 2 - bbox[1]), text, font=font, fill=WHITE)
    return img


def main() -> None:
    base = draw_icon()
    out = HERE / "app.ico"
    base.save(out, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
    base.resize((256, 256), Image.LANCZOS).save(HERE / "app.png")
    print(out)


if __name__ == "__main__":
    main()
