"""Build a Codex-format sprite atlas from the existing pet art.

Contract (from codex pet.json format / hatch-pet):
  1536 x 1872, 8 columns x 9 rows, each cell 192 x 208, transparent background.
  Row 0 idle, 1 running-right, 2 running-left, 3 waving, 4 jumping,
  5 failed, 6 waiting, 7 running, 8 review.

The source art is a single front-facing render, so every row is derived from
that one image: the torso/leg layers already split for the native renderer are
reused here to synthesise each state. No new art is invented.
"""
import pathlib
import sys

import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
SRC = ROOT / 'packages/native/Assets'
OUT = ROOT / 'packages/atlas/assets'

COLS, ROWS = 8, 9
CELL_W, CELL_H = 192, 208
SHEET_W, SHEET_H = COLS * CELL_W, ROWS * CELL_H  # 1536 x 1872

# 每行用多少帧（取自 hatch-pet 契约）
ROW_FRAMES = [6, 8, 8, 4, 5, 8, 6, 6, 6]
ROW_NAMES = ['idle', 'running-right', 'running-left', 'waving', 'jumping',
             'failed', 'waiting', 'running', 'review']

# 每行首帧时长（ms），用于 manifest 之外的参考；契约里最后一帧更长
ROW_DURATIONS = [
    [280, 110, 110, 140, 140, 320],
    [120] * 7 + [220],
    [120] * 7 + [220],
    [140] * 3 + [280],
    [140] * 4 + [280],
    [140] * 7 + [240],
    [150] * 5 + [260],
    [120] * 5 + [220],
    [150] * 5 + [280],
]


def load(name):
    p = SRC / name
    if not p.exists():
        raise SystemExit(f'missing asset: {p}')
    return Image.open(p).convert('RGBA')


def fit_cell(layer, torso, cell_w=CELL_W, cell_h=CELL_H, scale=1.0, dx=0, dy=0, angle=0.0):
    """把素材缩放进一格，保持脚底对齐、水平居中。"""
    sw, sh = layer.size
    k = min(cell_w / sw, cell_h / sh) * scale
    nw, nh = max(1, int(sw * k)), max(1, int(sh * k))
    img = layer.resize((nw, nh), Image.LANCZOS)

    canvas = Image.new('RGBA', (cell_w, cell_h), (0, 0, 0, 0))
    # 脚底贴底、水平居中，再叠加位移
    x = (cell_w - nw) // 2 + dx
    y = cell_h - nh + dy

    if abs(angle) > 0.05:
        img = img.rotate(angle, resample=Image.BICUBIC, expand=False)
    canvas.alpha_composite(img, (max(0, x), max(0, y)))
    return canvas


def compose(layer, torso, dx=0, dy=0, angle=0.0, scale=1.0):
    """腿在下、身子最后画，和原生渲染保持同一套遮挡关系。"""
    cell = Image.new('RGBA', (CELL_W, CELL_H), (0, 0, 0, 0))
    for part in (layer, torso):
        piece = fit_cell(part, torso, scale=scale, dx=dx, dy=dy, angle=angle)
        cell.alpha_composite(piece)
    return cell


def build():
    torso = load('pet-torso.png')
    legs = load('pet-leg-left.png')
    legs_r = load('pet-leg-right.png')
    shadow = load('pet-shadow.png')

    sheet = Image.new('RGBA', (SHEET_W, SHEET_H), (0, 0, 0, 0))

    def put(row, col, img):
        x = col * CELL_W
        y = row * CELL_H
        sheet.alpha_composite(img, (x, y))

    # ---- 0 idle：轻微呼吸起伏 ----
    bob = [0, -1, -2, -2, -1, 0]
    for c in range(ROW_FRAMES[0]):
        put(0, c, compose(legs, torso, dy=bob[c]))

    # ---- 1 running-right：整体右倾 + 抬脚 ----
    for c in range(ROW_FRAMES[1]):
        lift = [6, 4, 1, 0, 2, 5, 3, 1][c]
        put(1, c, compose(legs, torso, dx=2, dy=-lift - 1, angle=-2.0))

    # ---- 2 running-left：镜像方向 ----
    for c in range(ROW_FRAMES[2]):
        lift = [6, 4, 1, 0, 2, 5, 3, 1][c]
        put(2, c, compose(legs_r, torso, dx=-2, dy=-lift - 1, angle=2.0))

    # ---- 3 waving：一只脚抬起当"招手"，身体微倾 ----
    wave = [0, -2, -4, -3]
    for c in range(ROW_FRAMES[3]):
        put(3, c, compose(legs, torso, dy=wave[c], angle=-1.5))

    # ---- 4 jumping：起跳-顶点-落地 ----
    jump = [0, -6, -12, -8, -2]
    for c in range(ROW_FRAMES[4]):
        put(4, c, compose(legs, torso, dy=jump[c]))

    # ---- 5 failed：塌下去，整体下沉变矮 ----
    fail = [0, 1, 2, 3, 4, 5, 6, 7]
    for c in range(ROW_FRAMES[5]):
        put(5, c, compose(legs, torso, dy=fail[c], scale=1.0 - 0.004 * c))

    # ---- 6 waiting：小幅左右晃 ----
    sway = [0, 1, 2, 2, 1, 0]
    for c in range(ROW_FRAMES[6]):
        put(6, c, compose(legs, torso, dx=sway[c], angle=sway[c] * 0.8))

    # ---- 7 running：原地跑（正面）----
    for c in range(ROW_FRAMES[7]):
        bob2 = [0, -1, -2, -1, 0, -1]
        put(7, c, compose(legs, torso, dy=bob2[c]))

    # ---- 8 review：低头思考，轻微下沉 ----
    think = [0, 1, 1, 2, 1, 0]
    for c in range(ROW_FRAMES[8]):
        put(8, c, compose(legs, torso, dy=think[c]))

    OUT.mkdir(parents=True, exist_ok=True)
    webp = OUT / 'spritesheet.webp'
    sheet.save(webp, 'WEBP', quality=90, method=6, lossless=False)
    png = OUT / 'spritesheet.png'
    sheet.save(png, 'PNG')

    print(f'atlas: {sheet.size[0]}x{sheet.size[1]} (expect {SHEET_W}x{SHEET_H})')
    print(f'webp: {webp} {webp.stat().st_size/1024:.1f} KB')
    print(f'png : {png} {png.stat().st_size/1024:.1f} KB')
    return sheet


if __name__ == '__main__':
    build()