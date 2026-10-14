"""Generate the App Remote icons. Requires Pillow: py -3 -m pip install Pillow."""

from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src" / "AppRemote"
WEB = APP / "wwwroot"
ASSETS = APP / "Assets"
ASSETS.mkdir(exist_ok=True)

size = 1024
icon = Image.new("RGBA", (size, size), (0, 0, 0, 0))
gradient = Image.new("RGBA", (size, size))
pixels = gradient.load()
for y in range(size):
    t = y / (size - 1)
    color = (round(26 - 15 * t), round(111 - 64 * t), round(191 - 86 * t), 255)
    for x in range(size):
        pixels[x, y] = color
mask = Image.new("L", (size, size), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, 1023, 1023), radius=210, fill=255)
icon.paste(gradient, (0, 0), mask)

draw = ImageDraw.Draw(icon)
# A display with a distinct tap target that stays legible at taskbar size.
draw.rounded_rectangle((172, 218, 852, 712), radius=74, fill=(6, 32, 69, 115))
draw.rounded_rectangle((162, 204, 842, 698), radius=72, fill=(248, 252, 255, 255))
draw.rounded_rectangle((198, 240, 806, 658), radius=43, fill=(11, 42, 76, 255))
draw.rounded_rectangle((210, 252, 794, 646), radius=37, outline=(54, 105, 150, 255), width=8)
draw.ellipse((394, 307, 630, 543), outline=(61, 217, 234, 255), width=31)
draw.ellipse((453, 366, 571, 484), fill=(61, 217, 234, 255))
draw.ellipse((487, 400, 537, 450), fill=(255, 255, 255, 255))
draw.rounded_rectangle((466, 696, 538, 780), radius=17, fill=(235, 246, 255, 255))
draw.rounded_rectangle((365, 766, 639, 812), radius=23, fill=(248, 252, 255, 255))

resampling = Image.Resampling.LANCZOS
icon.resize((180, 180), resampling).save(WEB / "apple-touch-icon.png", optimize=True)
icon.resize((192, 192), resampling).save(WEB / "icon-192.png", optimize=True)
icon.resize((512, 512), resampling).save(WEB / "icon-512.png", optimize=True)
icon.save(ASSETS / "app.ico", format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
icon.save(WEB / "favicon.ico", format="ICO", sizes=[(16, 16), (32, 32), (48, 48)])
