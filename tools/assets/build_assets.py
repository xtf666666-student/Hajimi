"""Build the DSH-special pet assets from the supplied 3D render.

White-studio cut-out of the standing cat, plus a head layer for parallax depth.
Outputs into packages/app/assets/.

Run: python tools/assets/build_assets.py
"""
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "assets-src/pet3d/ref.png"
OUT = ROOT / "packages/app/assets"
OUT.mkdir(parents=True, exist_ok=True)

TARGET_H = 460


def imread(path: Path) -> np.ndarray:
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_UNCHANGED)


def imwrite(path: Path, bgr: np.ndarray) -> None:
    ok, buf = cv2.imencode(".png", bgr)
    if not ok:
        raise RuntimeError(f"encode failed: {path}")
    buf.tofile(str(path))


src = imread(SRC)
if src.shape[2] == 4:
    src = cv2.cvtColor(src, cv2.COLOR_BGRA2BGR)
h, w = src.shape[:2]

# ---------------------------------------------------------------- 白色影棚背景
rgb = src.astype(np.int16)
high = rgb.min(axis=2)
low = rgb.max(axis=2)
bright = (high > 232) & ((low - high) < 14)

flood = bright.astype(np.uint8) * 255
flood = cv2.morphologyEx(flood, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
seed = np.zeros((h + 2, w + 2), np.uint8)
for x in range(0, w, 4):
    for y in (0, h - 1):
        if flood[y, x] == 255:
            cv2.floodFill(flood, seed, (x, y), 128)
for y in range(0, h, 4):
    for x in (0, w - 1):
        if flood[y, x] == 255:
            cv2.floodFill(flood, seed, (x, y), 128)
bg = flood == 128

# ---------------------------------------------------------------- 精修前景
mask = np.full((h, w), cv2.GC_PR_FGD, np.uint8)
mask[bg] = cv2.GC_BGD
ring = np.zeros((h, w), bool)
ring[: int(h * 0.02)] = True
ring[int(h * 0.985):] = True
ring[:, : int(w * 0.02)] = True
ring[:, int(w * 0.985):] = True
mask[ring & bg] = cv2.GC_BGD
bgd = np.zeros((1, 65), np.float64)
fgd = np.zeros((1, 65), np.float64)
cv2.grabCut(src, mask, None, bgd, fgd, 5, cv2.GC_INIT_WITH_MASK)
fg = np.isin(mask, [cv2.GC_FGD, cv2.GC_PR_FGD]).astype(np.uint8) * 255
fg = cv2.morphologyEx(fg, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, np.ones((15, 15), np.uint8))

n, labels, stats, _ = cv2.connectedComponentsWithStats(fg, 8)
keep = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
fg = (labels == keep).astype(np.uint8) * 255

contours, _ = cv2.findContours(fg, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
filled = np.zeros_like(fg)
cv2.drawContours(filled, contours, -1, 255, -1)
fg = filled

# ---------------------------------------------------------------- 软边 + 去白边
alpha = cv2.GaussianBlur(fg.astype(np.float32) / 255.0, (0, 0), 1.1)
core = cv2.erode(fg, np.ones((5, 5), np.uint8)).astype(np.float32) / 255.0
core = cv2.GaussianBlur(core, (0, 0), 1.6)
alpha = np.clip(np.maximum(alpha * 0.55, core), 0, 1)
alpha[alpha < 0.04] = 0

a = alpha[..., None]
white = np.array([255, 255, 255], np.float32)
decon = (src.astype(np.float32) - (1 - a) * white) / np.maximum(a, 0.05)
decon = np.where((alpha > 0.9)[..., None], src.astype(np.float32), decon)
rgba = np.dstack([np.clip(decon, 0, 255), alpha * 255]).astype(np.uint8)

scale = TARGET_H / rgba.shape[0]
body = cv2.resize(rgba, (int(rgba.shape[1] * scale), TARGET_H), interpolation=cv2.INTER_AREA)

ys, xs = np.where(body[..., 3] > 8)
x0, x1 = xs.min(), xs.max()
pad_x = int((x1 - x0) * 0.10)
x0 = max(0, x0 - pad_x)
x1 = min(body.shape[1] - 1, x1 + pad_x)
body = body[:, x0:x1 + 1]
imwrite(OUT / "pet-body.png", body)

# ---------------------------------------------------------------- 头部分层（视差）
bh, bw = body.shape[:2]
cx = bw / 2
cy = bh * 0.235
rx = bw * 0.40
ry = bh * 0.245
yy, xx = np.mgrid[0:bh, 0:bw].astype(np.float32)
d = np.sqrt(((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2)
head_mask = np.clip((1.0 - d) / 0.10, 0, 1) ** 0.75
head_mask = cv2.GaussianBlur(head_mask, (0, 0), 7)
head = body.copy()
head[..., 3] = (body[..., 3].astype(np.float32) * head_mask).astype(np.uint8)
imwrite(OUT / "pet-head.png", head)

# ---------------------------------------------------------------- 影子贴图
sh_w, sh_h = int(bw * 0.66), int(bh * 0.10)
shadow = Image.new("RGBA", (sh_w, sh_h), (0, 0, 0, 0))
sd = ImageDraw.Draw(shadow)
sd.ellipse([0, 0, sh_w - 1, sh_h - 1], fill=(60, 44, 30, 120))
shadow = shadow.filter(ImageFilter.GaussianBlur(sh_h * 0.28))
shadow.save(OUT / "pet-shadow.png")


def to_pil(rgba_bgra: np.ndarray) -> Image.Image:
    return Image.fromarray(cv2.cvtColor(rgba_bgra, cv2.COLOR_BGRA2RGBA))


body_img = to_pil(body)
icon = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
ImageDraw.Draw(icon).ellipse([6, 6, 506, 506], fill=(255, 246, 232, 255))
inner = body_img.copy()
inner.thumbnail((430, 430), Image.LANCZOS)
icon.alpha_composite(inner, ((512 - inner.width) // 2, (512 - inner.height) // 2 + 12))
icon.save(OUT / "icon.png")
icon.save(OUT / "icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
body_img.resize((64, 64), Image.LANCZOS).save(OUT / "tray.png")

print("pet-body", body.shape)
print("head anchor", round(cx / bw, 4), round(cy / bh, 4), round(rx / bw, 4), round(ry / bh, 4))
