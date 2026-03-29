from __future__ import annotations

from pathlib import Path
from PIL import Image, ImageChops, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "CodexAuthSwitcher.App" / "Assets"
ICO_PATH = ASSETS / "AppIcon.ico"
PNG_PATH = ASSETS / "AppIconPreview.png"

SIZE = 1024
BG_TOP = (14, 39, 89)
BG_BOTTOM = (37, 103, 238)
RING_WHITE = (246, 250, 255, 255)
RING_CYAN = (126, 232, 255, 255)
SHIELD_FILL = (247, 251, 255, 242)
KEYHOLE = (25, 77, 177, 255)
SHADOW = (8, 18, 44, 90)


def gradient_square(size: int) -> Image.Image:
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    pixels = image.load()
    for y in range(size):
        t = y / (size - 1)
        r = int(BG_TOP[0] * (1 - t) + BG_BOTTOM[0] * t)
        g = int(BG_TOP[1] * (1 - t) + BG_BOTTOM[1] * t)
        b = int(BG_TOP[2] * (1 - t) + BG_BOTTOM[2] * t)
        for x in range(size):
            pixels[x, y] = (r, g, b, 255)
    return image


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle((72, 72, size - 72, size - 72), radius=radius, fill=255)
    return mask


def make_arrow_layer(size: int) -> Image.Image:
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    box = (220, 220, size - 220, size - 220)
    width = 88

    draw.arc(box, start=145, end=350, fill=RING_WHITE, width=width)
    draw.arc(box, start=-28, end=168, fill=RING_CYAN, width=width)

    draw.polygon([(770, 300), (880, 330), (790, 405)], fill=RING_WHITE)
    draw.polygon([(250, 700), (145, 672), (235, 595)], fill=RING_CYAN)

    return layer


def make_shield_layer(size: int) -> Image.Image:
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    shield = [
        (size * 0.5, size * 0.30),
        (size * 0.66, size * 0.37),
        (size * 0.66, size * 0.55),
        (size * 0.50, size * 0.71),
        (size * 0.34, size * 0.55),
        (size * 0.34, size * 0.37),
    ]
    draw.polygon(shield, fill=SHIELD_FILL)
    draw.line(shield + [shield[0]], fill=(255, 255, 255, 235), width=16, joint="curve")

    draw.ellipse((462, 408, 562, 508), fill=KEYHOLE)
    draw.rounded_rectangle((490, 486, 534, 612), radius=18, fill=KEYHOLE)

    return layer


def make_highlight(size: int) -> Image.Image:
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    draw.ellipse((120, 78, 720, 460), fill=(255, 255, 255, 42))
    return layer.filter(ImageFilter.GaussianBlur(28))


def make_shadow(size: int) -> Image.Image:
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    draw.rounded_rectangle((108, 118, size - 76, size - 54), radius=260, fill=SHADOW)
    return layer.filter(ImageFilter.GaussianBlur(34))


def build_icon() -> Image.Image:
    shadow = make_shadow(SIZE)
    background = gradient_square(SIZE)
    mask = rounded_mask(SIZE, 260)
    rounded_background = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    rounded_background.paste(background, (0, 0), mask)

    border = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    border_draw = ImageDraw.Draw(border)
    border_draw.rounded_rectangle(
        (88, 88, SIZE - 88, SIZE - 88),
        radius=244,
        outline=(255, 255, 255, 76),
        width=12,
    )

    icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    icon.alpha_composite(shadow)
    icon.alpha_composite(rounded_background)
    icon.alpha_composite(make_highlight(SIZE))
    icon.alpha_composite(make_arrow_layer(SIZE))
    icon.alpha_composite(make_shield_layer(SIZE))
    icon.alpha_composite(border)

    return ImageChops.offset(icon, 0, -8)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    image = build_icon()
    image.save(PNG_PATH)
    image.save(
        ICO_PATH,
        format="ICO",
        sizes=[(256, 256), (128, 128), (96, 96), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)],
    )
    print(f"Generated {ICO_PATH}")
    print(f"Generated {PNG_PATH}")


if __name__ == "__main__":
    main()
