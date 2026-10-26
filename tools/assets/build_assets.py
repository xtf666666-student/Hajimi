"""Build layered 2.5D pet assets from the supplied photos.

Layers produced (PNG, transparent):
  body.png      full sitting cat, white paper + desk background removed
  head-idle.png same canvas, only the head ellipse opaque (parallax depth layer)
  face-*.png    circular close-up reaction faces (smirk / shock / grin)
  icon.png/.ico app icon, tray.png tray icon

Run: python tools/assets/build_assets.py
"""
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "assets-src/new"
OUT = ROOT / "packages/app/assets"
OUT.mkdir(parents=True, exist_ok=True)

BODY_MAX = 820
FACE_SIZE = 448


def imread(path: Path) -> np.ndarray:
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def imwrite(path: Path, bgr: np.ndarray) -> None:
    ok, buf = cv2.imencode(".png", bgr)
    if not ok:
        raise RuntimeError(f"encode failed: {path}")
    buf.tofile(str(path))


def background_mask(bgr: np.ndarray, desk_from: int | None = None) -> np.ndarray:
    rgb = bgr.astype(np.int16)
    high = rgb.min(axis=2)
    low = rgb.max(axis=2)
    mask = (high > 224) & ((low - high) < 26)
    if desk_from is not None:
        lum = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
        ys = np.arange(bgr.shape[0])[:, None]
        mask |= (lum < 92) & (ys >= desk_from)
    return mask


def flood_from_border(mask: np.ndarray) -> np.ndarray:
    h, w = mask.shape
    flood = (mask.astype(np.uint8)) * 255
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
    return flood == 128


def refine_with_grabcut(bgr: np.ndarray, bg: np.ndarray, iters: int = 4) -> np.ndarray:
    mask = np.full(bgr.shape[:2], cv2.GC_PR_FGD, np.uint8)
    mask[bg] = cv2.GC_BGD
    bgd = np.zeros((1, 65), np.float64)
    fgd = np.zeros((1, 65), np.float64)
    cv2.grabCut(bgr, mask, None, bgd, fgd, iters, cv2.GC_INIT_WITH_MASK)
    fg = np.isin(mask, [cv2.GC_FGD, cv2.GC_PR_FGD]).astype(np.uint8) * 255
    fg = cv2.morphologyEx(fg, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, np.ones((13, 13), np.uint8))
    n, labels, stats, _ = cv2.connectedComponentsWithStats(fg, 8)
    keep = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    biggest = (labels == keep).astype(np.uint8) * 255
    contours, _ = cv2.findContours(biggest, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    filled = np.zeros_like(biggest)
    cv2.drawContours(filled, contours, -1, 255, -1)
    return filled


def cut_out(bgr: np.ndarray, fg: np.ndarray, erode_px: int = 2, sigma: float = 1.4,
            bg_color=(246, 246, 244)):
    """Erode the mask slightly then feather, so no bright background rim survives."""
    kernel = np.ones((erode_px * 2 + 1, erode_px * 2 + 1), np.uint8)
    eroded = cv2.erode((fg > 127).astype(np.uint8) * 255, kernel)
    alpha = np.clip(cv2.GaussianBlur(eroded.astype(np.float32) / 255.0, (0, 0), sigma), 0, 1)
    a = alpha[..., None]
    bg = np.array(bg_color, np.float32)
    decon = (bgr.astype(np.float32) - (1 - a) * bg) / np.maximum(a, 0.04)
    decon = np.where((alpha > 0.75)[..., None], bgr.astype(np.float32), decon)
    # 轻微压暗边缘，避免发白的等高线
    edge = ((alpha > 0.02) & (alpha < 0.85))[..., None]
    decon = np.where(edge, decon * 0.94, decon)
    return np.dstack([np.clip(decon, 0, 255), alpha * 255]).astype(np.uint8)


def trim_square(rgba: np.ndarray, pad_ratio: float = 0.015):
    alpha = rgba[..., 3]
    ys, xs = np.where(alpha > 8)
    if len(ys) == 0:
        raise RuntimeError("empty mask")
    y0, y1, x0, x1 = ys.min(), ys.max(), xs.min(), xs.max()
    pad = int(max(y1 - y0, x1 - x0) * pad_ratio)
    y0, x0 = max(0, y0 - pad), max(0, x0 - pad)
    y1, x1 = min(rgba.shape[0] - 1, y1 + pad), min(rgba.shape[1] - 1, x1 + pad)
    return rgba[y0:y1 + 1, x0:x1 + 1], (x0, y0)


def ellipse_alpha(h: int, w: int, cx: float, cy: float, rx: float, ry: float, soft: float = 0.12) -> np.ndarray:
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    d = np.sqrt(((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2)
    return (np.clip((1.0 - d) / soft, 0, 1) ** 0.8).astype(np.float32)


def remove_gray_debris(rgba: np.ndarray, bottom_ratio: float = 0.80) -> np.ndarray:
    """底部残留的桌面灰块：低饱和、灰色调、位于右下方，直接清掉。"""
    b, g, r = (rgba[..., i].astype(np.int16) for i in range(3))
    spread = np.maximum(np.maximum(r, g), b) - np.minimum(np.minimum(r, g), b)
    lum = (r * 299 + g * 587 + b * 114) // 1000
    h, w = rgba.shape[:2]
    ys = np.arange(h)[:, None]
    xs = np.arange(w)[None, :]
    neutral = (
        (spread < 22)
        & (lum > 60)
        & (lum < 215)
        & (ys >= int(h * bottom_ratio))
        & (xs >= int(w * 0.45))
    )
    rgba[..., 3] = np.where(neutral, 0, rgba[..., 3])
    return rgba


def fade_bottom_right_corner(rgba: np.ndarray, x0: float = 0.895, y0: float = 0.845) -> np.ndarray:
    """右下角残留着桌面边缘的灰块，用柔和渐隐把这一角吃掉。"""
    h, w = rgba.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    sx = np.clip((w * x0 - xx) / (w * 0.09), 0, 1)
    sy = np.clip((h * y0 - yy) / (h * 0.11), 0, 1)
    keep = np.maximum(sx, sy)
    rgba[..., 3] = (rgba[..., 3].astype(np.float32) * keep).astype(np.uint8)
    return rgba


# ---------------------------------------------------------------- body
sit = imread(SRC / "sit.jpg")
sit_fg = refine_with_grabcut(sit, flood_from_border(background_mask(sit, desk_from=1000)), 4)
body_rgba = cut_out(sit, sit_fg, erode_px=2, sigma=1.3)
body_rgba = remove_gray_debris(body_rgba)
body_rgba = fade_bottom_right_corner(body_rgba)
body_crop, (bx, by) = trim_square(body_rgba, 0.012)

scale = BODY_MAX / max(body_crop.shape[:2])
body_out = cv2.resize(body_crop, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA) if scale < 1 else body_crop
imwrite(OUT / "body.png", body_out)

# head ellipse (original coordinates) -> head-only layer on the same canvas
head_cx, head_cy, head_rx, head_ry = 668.0, 326.0, 372.0, 300.0
mask = ellipse_alpha(sit.shape[0], sit.shape[1], head_cx, head_cy, head_rx, head_ry, 0.16)
head_rgba = body_rgba.copy()
head_rgba[..., 3] = (body_rgba[..., 3].astype(np.float32) * mask).astype(np.uint8)
head_crop = head_rgba[by:by + body_crop.shape[0], bx:bx + body_crop.shape[1]]
head_out = cv2.resize(head_crop, (body_out.shape[1], body_out.shape[0]), interpolation=cv2.INTER_AREA)
imwrite(OUT / "head-idle.png", head_out)

hx = (head_cx - bx) / body_crop.shape[1]
hy = (head_cy - by) / body_crop.shape[0]
hrx = head_rx / body_crop.shape[1]
hry = head_ry / body_crop.shape[0]

# ---------------------------------------------------------------- reaction faces
def face_layer(name: str, out_name: str, radius_ratio: float = 0.40) -> None:
    """表情特写：整圆都落在猫脸上，不做抠像，避免出现白色光晕。"""
    bgr = imread(SRC / f"{name}.jpg")
    h, w = bgr.shape[:2]
    cx, cy = w / 2, h * 0.47
    r = min(w, h) * radius_ratio
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    alpha = np.clip((r - d) / 3.0, 0, 1)
    # 边缘轻微压暗，和身体层融合
    edge = ((alpha > 0.05) & (alpha < 0.98))[..., None]
    rgb = np.where(edge, bgr.astype(np.float32) * 0.82, bgr.astype(np.float32))
    rgba = np.dstack([np.clip(rgb, 0, 255), alpha * 255]).astype(np.uint8)
    crop, _ = trim_square(rgba, 0.0)
    side = max(crop.shape[:2])
    canvas = np.zeros((side, side, 4), np.uint8)
    oy, ox = (side - crop.shape[0]) // 2, (side - crop.shape[1]) // 2
    canvas[oy:oy + crop.shape[0], ox:ox + crop.shape[1]] = crop
    canvas = cv2.resize(canvas, (FACE_SIZE, FACE_SIZE), interpolation=cv2.INTER_AREA)
    imwrite(OUT / out_name, canvas)


face_layer("smirk", "face-smirk.png")
face_layer("shock", "face-shock.png")
face_layer("grin", "face-grin.png")

# ---------------------------------------------------------------- icon + tray
def to_pil(rgba_bgra: np.ndarray) -> Image.Image:
    return Image.fromarray(cv2.cvtColor(rgba_bgra, cv2.COLOR_BGRA2RGBA))


body_img = to_pil(body_out)
icon = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
ImageDraw.Draw(icon).ellipse([8, 8, 504, 504], fill=(255, 246, 232, 255))
inner = body_img.copy()
inner.thumbnail((464, 464), Image.LANCZOS)
icon.alpha_composite(inner, ((512 - inner.width) // 2, (512 - inner.height) // 2 + 8))
icon.save(OUT / "icon.png")
icon.save(OUT / "icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
body_img.resize((64, 64), Image.LANCZOS).save(OUT / "tray.png")

legacy = ROOT / "assets-src/hajimi-original.png"
if legacy.exists():
    try:
        Image.open(legacy).save(OUT / "hajimi-original.png")
    except PermissionError:
        print("skip hajimi-original.png (locked)")

print("body", body_out.shape)
print(
    "head anchor",
    f"left={(hx - hrx):.4f} top={(hy - hry):.4f} width={(2 * hrx):.4f} height={(2 * hry):.4f}",
)
print("assets written")
