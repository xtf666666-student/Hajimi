"""Compose a README preview image from real app screenshots."""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
SHOTS = ROOT / "packages/app/selftest-artifacts"
DOCS = ROOT / "docs"
DOCS.mkdir(exist_ok=True)

W, H = 1280, 720
bg = Image.new("RGB", (W, H))
draw = ImageDraw.Draw(bg)
for y in range(H):
    t = y / H
    draw.line([(0, y), (W, y)], fill=(int(26 + 16 * t), int(34 + 22 * t), int(52 + 30 * t)))

# 模拟一个正在被摸鱼的编辑器窗口
draw.rounded_rectangle([40, 40, 470, 300], 16, fill=(38, 48, 66))
draw.rounded_rectangle([40, 40, 470, 92], 16, fill=(48, 60, 82))
draw.rounded_rectangle([64, 64, 96, 80], 6, fill=(92, 108, 134))
draw.rounded_rectangle([110, 64, 230, 80], 6, fill=(78, 92, 116))
for i, width in enumerate((300, 260, 320, 190)):
    draw.rounded_rectangle([64, 120 + i * 34, 64 + width, 136 + i * 34], 5, fill=(64, 76, 98))
draw.rectangle([0, H - 40, W, H], fill=(18, 24, 34))
for i in range(5):
    draw.rounded_rectangle([60 + i * 56, H - 32, 96 + i * 56, H - 8], 8, fill=(46, 56, 74))

pet = Image.open(SHOTS / "pet-idle.png").convert("RGBA")
scale = 620 / pet.height
pet = pet.resize((int(pet.width * scale), 620), Image.LANCZOS)
shadow = Image.new("RGBA", pet.size, (12, 8, 4, 150))
shadow.putalpha(pet.split()[3].filter(ImageFilter.GaussianBlur(10)).point(lambda v: int(v * 0.5)))
glow = Image.new("RGBA", pet.size, (255, 226, 172, 255))
glow.putalpha(pet.split()[3].filter(ImageFilter.GaussianBlur(20)).point(lambda v: int(v * 0.40)))

px, py = W - pet.width - 18, H - pet.height - 6
bg = bg.convert("RGBA")
bg.alpha_composite(glow, (px - 8, py + 4))
bg.alpha_composite(shadow, (px + 10, py + 16))
bg.alpha_composite(pet, (px, py))

settings = Image.open(SHOTS / "settings.png").convert("RGBA")
settings = settings.resize((int(settings.width * 0.8), int(settings.height * 0.8)), Image.LANCZOS)
s_shadow = Image.new("RGBA", settings.size, (0, 0, 0, 130))
s_shadow.putalpha(settings.split()[3].filter(ImageFilter.GaussianBlur(14)).point(lambda v: int(v * 0.6)))
bg.alpha_composite(s_shadow, (528, 46))
bg.alpha_composite(settings, (514, 32))

menu = Image.open(SHOTS / "menu.png").convert("RGBA")
menu = menu.resize((int(menu.width * 0.86), int(menu.height * 0.86)), Image.LANCZOS)
m_shadow = Image.new("RGBA", menu.size, (0, 0, 0, 120))
m_shadow.putalpha(menu.split()[3].filter(ImageFilter.GaussianBlur(10)).point(lambda v: int(v * 0.6)))
bg.alpha_composite(m_shadow, (1082, 96))
bg.alpha_composite(menu, (1072, 88))

bg.convert("RGB").save(DOCS / "preview.png")
for name in ("pet-idle", "pet-happy", "settings", "menu"):
    src = SHOTS / f"{name}.png"
    if src.exists():
        Image.open(src).save(DOCS / f"{name}.png")
print("docs/preview.png written")
