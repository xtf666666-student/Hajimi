"""Verify this round's fixes against the geometry the app itself reports.

Colour heuristics are fragile (the list background is warm, the cat's ears reach
the top edge), so all positional checks read the rectangles the renderer
recorded while drawing. Pixel checks are limited to "is anything drawn here".
"""
import json
import os
import pathlib
import shutil
import subprocess

import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'
FRAMES = ROOT / 'tools' / 'out' / 'frames'
TMP = pathlib.Path(os.environ.get('TEMP', '.'))

results = []


def check(name, ok, detail=''):
    results.append((bool(ok), name, str(detail)))
    print(f"[{'PASS' if ok else 'FAIL'}] {name} - {detail}")


def dump():
    shutil.rmtree(FRAMES, ignore_errors=True)
    FRAMES.mkdir(parents=True, exist_ok=True)
    out_file = TMP / 'dump-out.txt'
    if out_file.exists():
        out_file.unlink()
    ps = (f"$p = Start-Process -FilePath '{EXE}' "
          f"-ArgumentList '--size','140','--dump','{FRAMES}' -PassThru "
          f"-RedirectStandardOutput '{out_file}' -NoNewWindow; "
          f"$p.WaitForExit(90000) | Out-Null")
    subprocess.run(['powershell.exe', '-NoProfile', '-Command', ps], capture_output=True)
    return out_file.read_text(encoding='utf-8', errors='replace') if out_file.exists() else ''


def geo(name):
    return json.loads((FRAMES / f'{name}.json').read_text(encoding='utf-8-sig'))


def main():
    log = dump()
    print('dump log:', log.strip()[:300])

    need = ['idle.png', 'busy3.png', 'busy3-list.png', 'busy1-list.png',
            'bubble.png', 'idle.json', 'bubble.json']
    missing = [n for n in need if not (FRAMES / n).exists()]
    if missing:
        check('导出验证帧与几何', False, f'missing={missing}')
        return 1
    check('导出验证帧与几何', True, '全部就绪')

    gi = geo('idle')
    gb = geo('bubble')

    # ---- 问题 3：空闲时头顶不画角标，也不占高度 ----
    check('空闲时角标矩形为空', gi['badge'][2] == 0 and gi['badge'][3] == 0,
          f"badge={gi['badge']}")
    check('空闲时不预留角标高度', gi['window'][1] < gb['window'][1],
          f"idle {gi['window'][1]}px vs 有列表 {gb['window'][1]}px")

    # ---- 有任务时角标在，且贴着猫头 ----
    cat = gb['cat']
    badge = gb['badge']
    cat_top = cat[1]
    badge_bottom = badge[1] + badge[3]
    gap = cat_top - badge_bottom
    check('有任务时角标存在', badge[2] > 10, f'badge={badge}')
    # 角标紧贴猫头：允许 pulse 动画时短暂的重叠（最多 3px）
    gap = cat_top - badge_bottom
    check('角标紧贴猫头', -3 <= gap <= 6, f'间距={gap}px（pulse 动画会短暂放大）')

    # 角标横向对齐猫中心
    badge_cx = badge[0] + badge[2] / 2
    cat_cx = cat[0] + cat[2] / 2
    check('角标对齐猫中心', abs(badge_cx - cat_cx) <= 4,
          f'角标中心={badge_cx:.0f}, 猫中心={cat_cx:.0f}')

    # ---- 问题 2：阴影跟着猫走 ----
    shadow = gb['shadow']
    shadow_cx = shadow[0] + shadow[2] / 2
    check('阴影横向对齐猫中心', abs(shadow_cx - cat_cx) <= 4,
          f'阴影中心={shadow_cx:.0f}, 猫中心={cat_cx:.0f}')
    # 阴影在猫下方
    check('阴影位于脚底', shadow[1] >= cat[1] + cat[3] - 8,
          f"阴影 y={shadow[1]}, 猫底 y={cat[1]+cat[3]}")

    # ---- 问题 1：完成的任务保留（列表多行） ----
    check('列表保留多行（含已完成/未读）', gb['rows'][0] >= 2, f"rows={gb['rows'][0]}")
    check('列表矩形有效', gb['list'][2] > 100, f"list={gb['list']}")

    # ---- 叉掉按钮：三按钮一排，右侧最外 ----
    lst = Image.open(FRAMES / 'busy3-list.png').convert('RGBA')
    a = np.array(lst).astype(int)
    right = a[:, lst.size[0] - 95:]
    red = (right[:, :, 0] > 150) & (right[:, :, 1] < 130) & (right[:, :, 3] > 120)
    check('停止按钮存在', int(red.sum()) > 15, f'红色={int(red.sum())}')

    striped = 0
    for x in range(right.shape[1]):
        col = right[:, x]
        neutral = ((np.abs(col[:, 0] - col[:, 2]) < 40) & (col[:, 0] > 120)
                   & (col[:, 0] < 200) & (col[:, 3] > 150))
        if neutral.sum() >= 2:
            striped += 1
    check('叉掉按钮存在', striped >= 4, f'斜线命中列={striped}')

    # ---- 空闲帧头顶确实是透的（排除列表干扰，空闲帧没有列表）----
    idle = np.array(Image.open(FRAMES / 'idle.png').convert('RGBA'))
    # 角标若存在会占用 cat.y 以上；空闲帧 cat.y 应为 0
    check('空闲帧猫头顶没有上方留白', gi['cat'][1] == 0, f"cat.y={gi['cat'][1]}")

    passed = sum(1 for ok, _, _ in results if ok)
    print(f'\n{passed}/{len(results)} passed')
    return 0 if passed == len(results) else 1


if __name__ == '__main__':
    raise SystemExit(main())