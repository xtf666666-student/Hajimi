"""Verify the four reported fixes using the app's own frame dump.

Layered windows cannot be captured reliably by screen-grab or PrintWindow, so the
app exports its real render output via --dump. That is the actual drawing code.
"""
import pathlib
import shutil
import subprocess
import sys

import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'
OUT = ROOT / 'tools' / 'out' / 'frames'

results = []


def check(name, ok, detail=''):
    results.append((name, bool(ok), str(detail)))
    print(f"[{'PASS' if ok else 'FAIL'}] {name} - {detail}")


def load(name):
    return Image.open(OUT / name).convert('RGBA')


def top_band_width(img, band=22):
    """角标区域里非透明像素的横向跨度。"""
    a = np.array(img)
    alpha = a[:, :, 3]
    sub = alpha[:band]
    cols = np.where(sub.max(axis=0) > 120)[0]
    if len(cols) == 0:
        return 0
    return int(cols.max() - cols.min() + 1)


def content_rows(img):
    a = np.array(img)
    alpha = a[:, :, 3]
    return [(y, int((alpha[y] > 25).sum())) for y in range(a.shape[0])]


def foot_gap(img):
    """脚底最后一行到窗口底边之间，阴影是否存在（半透明像素）。"""
    a = np.array(img)
    alpha = a[:, :, 3]
    h = a.shape[0]
    rows = [y for y in range(h) if (alpha[y] > 200).sum() > 3]
    if not rows:
        return None
    last_cat = max(rows)
    below = alpha[last_cat + 1:]
    if below.size == 0:
        return ('no_room', last_cat, h)
    return ('ok' if below.max() > 15 else 'empty', last_cat, h, int(below.max()))


def main():
    shutil.rmtree(OUT, ignore_errors=True)
    OUT.mkdir(parents=True, exist_ok=True)
    subprocess.run([str(EXE), '--size', '140', '--dump', str(OUT)],
                   capture_output=True, timeout=120)

    for f in ('idle.png', 'busy3.png', 'busy3-list.png', 'busy1-list.png'):
        if not (OUT / f).exists():
            print('missing frame:', f)
            return 1

    idle = load('idle.png')
    busy3 = load('busy3.png')
    list3 = load('busy3-list.png')
    list1 = load('busy1-list.png')

    # ---- 问题 1：空闲时只留灰胶囊，不显示 0 ----
    w_idle = top_band_width(idle)
    w_busy = top_band_width(busy3)
    check('空闲态角标是窄的灰胶囊（无数字）', 14 <= w_idle <= 26, f'{w_idle}px')
    check('有任务时角标含数字（比空闲宽）', w_busy > w_idle + 4, f'空闲 {w_idle}px vs 有任务 {w_busy}px')

    # ---- 问题 4：阴影紧贴脚底 ----
    g = foot_gap(idle)
    if isinstance(g, tuple) and g[0] == 'ok':
        check('阴影紧贴脚底', True, f'脚底行={g[1]} 窗口高={g[2]} 阴影alpha={g[3]}')
    else:
        check('阴影紧贴脚底', False, str(g))

    # ---- 问题 2：列表内容完整，无大片空白 ----
    rows = content_rows(list3)
    h3 = len(rows)
    first = next((y for y, c in rows if c > 6), 0)
    last = h3 - 1 - next((y for y, c in reversed(rows) if c > 6), 0)
    gaps = 0
    run = 0
    for y in range(first, last + 1):
        if rows[y][1] <= 6:
            run += 1
        else:
            if run > 10:
                gaps += 1
            run = 0
    check('3 条会话的列表与猫之间无大空隙', gaps == 0,
          f'内容 {first}..{last}, 空隙段={gaps}')

    rows1 = content_rows(list1)
    h1 = len(rows1)
    first1 = next((y for y, c in rows1 if c > 6), 0)
    last1 = h1 - 1 - next((y for y, c in reversed(rows1) if c > 6), 0)
    gaps1 = 0
    run = 0
    for y in range(first1, last1 + 1):
        if rows1[y][1] <= 6:
            run += 1
        else:
            if run > 10:
                gaps1 += 1
            run = 0
    check('1 条会话的列表同样紧凑（无空白）', gaps1 == 0,
          f'内容 {first1}..{last1}, 空隙段={gaps1}')

    # ---- 列表高度随行数变化（问题 2 的根因）----
    check('列表高度随会话数自适应', list1.size[1] < list3.size[1],
          f'1 条={list1.size[1]}px, 3 条={list3.size[1]}px')

    print('\n=== 汇总 ===')
    passed = sum(1 for _, ok, _ in results if ok)
    print(f'{passed}/{len(results)} passed')
    return 0 if passed == len(results) else 1


if __name__ == '__main__':
    raise SystemExit(main())