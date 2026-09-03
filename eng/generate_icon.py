"""Generate the checked-in multi-resolution Windows icon from simple vector primitives."""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter


SIZE = 1024
ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "src" / "WindPlay.App" / "Assets" / "WindPlay.ico"


def rounded_mask(box: tuple[int, int, int, int], radius: int) -> Image.Image:
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)
    return mask


canvas = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
shell = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
shell_mask = rounded_mask((48, 48, 976, 976), 220)
pixels = shell.load()
for y in range(SIZE):
    t = y / (SIZE - 1)
    for x in range(SIZE):
        r = int(112 * (1 - t) + 38 * t)
        g = int(196 * (1 - t) + 83 * t)
        b = int(255 * (1 - t) + 199 * t)
        pixels[x, y] = (r, g, b, shell_mask.getpixel((x, y)))
canvas.alpha_composite(shell)

shine = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
ImageDraw.Draw(shine).ellipse((90, -240, 934, 410), fill=(255, 255, 255, 34))
shine.putalpha(Image.composite(shine.getchannel("A"), Image.new("L", (SIZE, SIZE)), shell_mask))
canvas.alpha_composite(shine)

shadow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
ImageDraw.Draw(shadow).rounded_rectangle((170, 220, 854, 750), radius=78, fill=(4, 33, 83, 115))
shadow = shadow.filter(ImageFilter.GaussianBlur(46))
canvas.alpha_composite(shadow, (0, 34))

draw = ImageDraw.Draw(canvas)
draw.rounded_rectangle((170, 190, 854, 718), radius=80, fill=(241, 249, 255, 244), outline=(255, 255, 255, 255), width=20)
draw.rounded_rectangle((220, 240, 804, 636), radius=40, fill=(12, 36, 71, 255))
draw.ellipse((260, 240, 760, 520), fill=(255, 255, 255, 26))
draw.polygon(((512, 494), (328, 800), (696, 800)), fill=(250, 253, 255, 255), outline=(208, 235, 255, 255), width=14)
draw.ellipse((484, 680, 540, 736), fill=(168, 218, 255, 210))

OUTPUT.parent.mkdir(parents=True, exist_ok=True)
canvas.save(OUTPUT, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
