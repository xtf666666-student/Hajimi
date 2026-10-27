"""Split the pet into torso + two feet so the cat can actually run on its feet.

Source art: 376x460 chunky standing cat. Measured silhouette (tools/measure_feet.py):

    y=404  [75,293]                 contiguous belly
    y=406  [74,165] [170,293]       <- feet separate here
    y=420  [62,133] [232,306]
    y=434  [66,124] [139,153] [247,305]
    y=440  empty

Two facts drive the layout:

  1. The region between the feet at y>406 is NOT cat body - it is the art's baked
     ground shadow. Leaving it inside the foot layers makes it travel with a foot
     and tear a pale arc between the legs, so it is stripped out entirely. The
     pet then stands on the separately generated soft shadow (pet-shadow.png),
     and a lifted foot simply reveals the wallpaper, which is correct.
  2. torso is hard-cut at CUT and painted LAST, hiding the ankle seam. Feet reach
     up to FOOT_TOP, an ordinary body strip the torso paints over, which is what
     lets a foot rotate without opening a hole.

Runtime draw order: feet -> torso (torso last).

Run: python tools/assets/build_layers.py
"""
import pathlib

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = pathlib.Path(__file__).resolve().parents[2]
ASSETS = ROOT / 'packages/native/Assets'
SRC = ASSETS / 'pet-body.png'

CUT = 406.0        # torso bottom == ankle line == top of the baked shadow band
FOOT_TOP = 396.0   # feet reach up this far; hidden under the torso
SPLIT_X = 150.0    # between the two feet
SHADOW_X0, SHADOW_X1 = 110.0, 292.0
SHADOW_Y0, SHADOW_Y1 = CUT, 452.0

im = Image.open(SRC).convert('RGBA')
arr = np.array(im).astype(np.float32)
rgb = arr[:, :, :3]
alpha = arr[:, :, 3] / 255.0
H, W = alpha.shape
ys, xs = np.mgrid[0:H, 0:W].astype(np.float32)

# ---------------------------------------------------------------- 1. 找出烘焙的地面阴影
mx = rgb.max(axis=2)
mn = rgb.min(axis=2)
spread = mx - mn
lum = rgb.mean(axis=2)

ground = ((xs >= SHADOW_X0) & (xs <= SHADOW_X1) &
          (ys >= SHADOW_Y0) & (ys <= SHADOW_Y1) &
          (spread < 28) & (lum > 95) & (lum < 250))

# ---------------------------------------------------------------- 2. 切分
torso_a = np.where(ys < CUT, alpha, 0.0)

# 脚：从 FOOT_TOP 往下，剔除地面阴影（否则会随脚移动、在两腿间撕出亮弧）
feet_a = np.where((ys >= FOOT_TOP) & ~ground, alpha, 0.0)
left_a = np.where(xs < SPLIT_X, feet_a, 0.0)
right_a = np.where(xs >= SPLIT_X, feet_a, 0.0)


def compose(a):
    out = np.dstack([rgb, np.clip(a, 0, 1) * 255.0]).astype(np.uint8)
    return Image.fromarray(out, 'RGBA')


compose(torso_a).save(ASSETS / 'pet-torso.png')
compose(left_a).save(ASSETS / 'pet-leg-left.png')
compose(right_a).save(ASSETS / 'pet-leg-right.png')

# ---------------------------------------------------------------- 3. 独立柔和阴影
SH_H = 26
sh = Image.new('RGBA', (W, SH_H), (0, 0, 0, 0))
ImageDraw.Draw(sh).ellipse([W * 0.20, 0, W * 0.80, SH_H - 1], fill=(52, 36, 20, 124))
sh = sh.filter(ImageFilter.GaussianBlur(5))
sh.save(ASSETS / 'pet-shadow.png')

# 清掉旧的底座层（已废弃）
stale = ASSETS / 'pet-base.png'
if stale.exists():
    stale.unlink()


def bbox(a):
    rows = np.where(a.max(axis=1) > 0.02)[0]
    cols = np.where(a.max(axis=0) > 0.02)[0]
    if len(rows) == 0 or len(cols) == 0:
        return None
    return (int(cols.min()), int(rows.min()), int(cols.max()), int(rows.max()))


print('canvas', (W, H))
print('torso  bbox', bbox(torso_a))
print('foot L bbox', bbox(left_a))
print('foot R bbox', bbox(right_a))
print('baked shadow stripped px', int(ground.sum()))
print(f'cut={CUT} foot_top={FOOT_TOP} split_x={SPLIT_X}')
print('ankle L', (100, int(CUT)), 'ankle R', (272, int(CUT)))