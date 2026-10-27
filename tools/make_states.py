"""Compose the three state frames into one comparison sheet for the README."""
import pathlib

from PIL import Image, ImageDraw, ImageFont

ROOT = pathlib.Path(__file__).resolve().parents[1]
DOCS = ROOT / 'docs'

items = [
    ('state-idle.png', 'no task running: grey dot only'),
    ('state-busy.png', 'tasks running: shows the count'),
    ('state-list.png', 'click the count: list opens'),
]

loaded = []
for name, label in items:
    p = DOCS / name
    if not p.exists():
        raise SystemExit(f'missing {p}')
    loaded.append((Image.open(p).convert('RGBA'), label))

pad = 14
W = sum(img.width for img, _ in loaded) + pad * (len(loaded) + 1)
H = max(img.height for img, _ in loaded) + pad * 2 + 20
sheet = Image.new('RGBA', (W, H), (247, 248, 250, 255))
draw = ImageDraw.Draw(sheet)

x = pad
for img, label in loaded:
    sheet.alpha_composite(img, (x, pad))
    draw.text((x + 4, pad + img.height + 3), label, fill=(90, 80, 68, 255))
    x += img.width + pad

sheet = sheet.resize((sheet.width * 2, sheet.height * 2), Image.LANCZOS)
sheet.convert('RGB').save(DOCS / 'states.png')
print('saved states.png', sheet.size)