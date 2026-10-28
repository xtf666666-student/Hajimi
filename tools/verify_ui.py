"""Verify the sidebar bubble placement and the row reply/stop buttons."""
import pathlib
import shutil
import subprocess

import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'
OUT = ROOT / 'tools' / 'out' / 'frames'

results = []


def check(name, ok, detail=''):
    results.append((bool(ok), name, str(detail)))
    print(f"[{'PASS' if ok else 'FAIL'}] {name} - {detail}")


def main():
    shutil.rmtree(OUT, ignore_errors=True)
    OUT.mkdir(parents=True, exist_ok=True)
    subprocess.run([str(EXE), '--size', '140', '--dump', str(OUT)],
                   capture_output=True, timeout=120)

    bubble = np.array(Image.open(OUT / 'bubble.png').convert('RGBA'))
    lst = np.array(Image.open(OUT / 'busy3-list.png').convert('RGBA'))
    idle = np.array(Image.open(OUT / 'idle.png').convert('RGBA'))

    h, w = bubble.shape[:2]
    print(f'bubble frame: {w}x{h}')

    alpha = bubble[:, :, 3]

    # ---- 气泡位置：用程序导出的真实矩形判断，不靠颜色猜 ----
    import json
    meta = json.loads((OUT / 'bubble.json').read_text(encoding='utf-8'))
    print('geometry:', meta)
    bub = meta['bubble']
    list_rect = meta['list']
    badge = meta['badge']

    check('侧边气泡已绘制', bub[2] > 40 and bub[3] > 20, f'气泡矩形={bub}')

    rect = lambda r: (r[0], r[1], r[0] + r[2], r[1] + r[3])
    bx0, by0, bx1, by1 = rect(bub)
    lx0, ly0, lx1, ly1 = rect(list_rect)
    overlap = not (bx1 <= lx0 or lx1 <= bx0 or by1 <= ly0 or ly1 <= by0)
    check('气泡与列表矩形不相交', not overlap,
          f'气泡 y {by0}..{by1}, 列表 y {ly0}..{ly1}')

    # 气泡必须在角标下方（角标贴着猫头，气泡对齐猫头更低处）
    check('气泡不与角标重叠', by0 >= badge[1] + badge[3] - 2,
          f'气泡顶 y={by0}, 角标底 y={badge[1] + badge[3]}')

    # 气泡贴猫的左侧：左边缘离窗口左边界不远，右边缘小于窗口宽度
    check('气泡挂在猫的侧面', bx1 <= meta['window'][0] and bx0 >= 0,
          f'气泡 x {bx0}..{bx1}, 窗口宽 {meta["window"][0]}')

    # ---- 列表行按钮 ----
    # 右侧一列应有两组小圆（回复/停止），颜色分别是绿和红
    a = lst.astype(int)
    right = a[:, lst.shape[1] - 90:]
    red = (right[:, :, 0] > 150) & (right[:, :, 1] < 130) & (right[:, :, 2] < 130) & (right[:, :, 3] > 150)
    green = (right[:, :, 1] > 110) & (right[:, :, 0] < 130) & (right[:, :, 3] > 120)
    check('列表右侧有停止按钮（红色方块）', int(red.sum()) > 20, f'红色像素={int(red.sum())}')
    check('列表右侧有回复按钮（绿色箭头）', int(green.sum()) > 20, f'绿色像素={int(green.sum())}')

    # 三个会话行应该各有一组按钮 => 红色方块成 3 段
    ys = np.where(red.any(axis=1))[0]
    segments = 1 if len(ys) else 0
    for i in range(1, len(ys)):
        if ys[i] - ys[i - 1] > 3:
            segments += 1
    check('每个会话行都有按钮', segments == 3, f'红色分段={segments}')

    # ---- 空闲态仍然没有数字 ----
    ial = idle[:, :, 3][:22]
    cols = np.where(ial.max(axis=0) > 120)[0]
    bw = int(cols.max() - cols.min() + 1) if len(cols) else 0
    check('空闲态仍是窄灰胶囊', 14 <= bw <= 26, f'{bw}px')

    passed = sum(1 for ok, _, _ in results if ok)
    print(f'\n{passed}/{len(results)} passed')
    return 0 if passed == len(results) else 1


if __name__ == '__main__':
    raise SystemExit(main())