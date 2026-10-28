"""Self-test for the native pet.

Layered windows (WS_EX_LAYERED via UpdateLayeredWindow) cannot be captured by
screen grab or PrintWindow once the desktop compositor drops their backing store,
so all pixel assertions use the app's own --dump output. That is the real
drawing code, just written to disk instead of the screen.
"""
import ctypes
import ctypes.wintypes
import json
import pathlib
import shutil
import subprocess
import time

import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools' / 'out'
FRAMES = OUT / 'frames'
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()
TITLE = 'HajimiPet'

checks = []


def check(name, ok, detail=''):
    checks.append({'name': name, 'ok': bool(ok), 'detail': str(detail)})
    print(f"[selftest] {'PASS' if ok else 'FAIL'} {name} - {detail}")


class RECT(ctypes.Structure):
    _fields_ = [('l', ctypes.c_long), ('t', ctypes.c_long),
                ('r', ctypes.c_long), ('b', ctypes.c_long)]


def find():
    hwnd = user32.FindWindowW(None, TITLE)
    if not hwnd:
        return None
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return hwnd, (r.l, r.t, r.r, r.b)


def kill():
    subprocess.run(['powershell.exe', '-NoProfile', '-Command',
                    "Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }"],
                   capture_output=True)


def dsh_counts():
    home = pathlib.Path.home() / '.dsh'
    src = home / 'storages' / 'session_projcache' / 'sessions'
    if not src.exists():
        return None
    import re
    active = total = 0
    for f in src.glob('session-*.json'):
        total += 1
        try:
            t = f.read_text(encoding='utf-8', errors='replace')
        except OSError:
            continue
        k = re.search(r'"lastStepBoundary"\s*:\s*\{\s*"kind"\s*:\s*"([a-z]+)"', t)
        n = re.search(r'"openTurnStartSeq"\s*:\s*(\d+)', t)
        if k and n and k.group(1) == 'start' and int(n.group(1)) > 0:
            active += 1
    return active, total


def dump_frames():
    shutil.rmtree(FRAMES, ignore_errors=True)
    FRAMES.mkdir(parents=True, exist_ok=True)
    subprocess.run([str(EXE), '--size', '140', '--dump', str(FRAMES)],
                   capture_output=True, timeout=120)


def band_width(img):
    al = np.array(img)[:, :, 3][:22]
    cols = np.where(al.max(axis=0) > 120)[0]
    return int(cols.max() - cols.min() + 1) if len(cols) else 0


def main():
    kill()
    dump_frames()

    missing = [n for n in ('idle.png', 'busy3.png', 'busy3-list.png', 'busy1-list.png')
               if not (FRAMES / n).exists()]
    check('导出渲染帧（自检依据）', not missing, 'missing=' + str(missing))

    if not missing:
        idle = Image.open(FRAMES / 'idle.png').convert('RGBA')
        busy = Image.open(FRAMES / 'busy3.png').convert('RGBA')
        lst = Image.open(FRAMES / 'busy3-list.png').convert('RGBA')

        a = np.array(idle)
        check('透明背景（左上角 alpha=0）', a[0, 0, 3] == 0, f'alpha={a[0, 0, 3]}')
        opaque = int((a[:, :, 3] > 200).sum())
        check('猫本体已渲染', opaque > 2000, f'不透明像素={opaque}')

        # 空闲帧变矮后猫耳贴到顶部，像素扫描会误判成角标；
        # 用渲染器上报的矩形判断角标是否存在。
        import json as _json
        gi = _json.loads((FRAMES / 'idle.json').read_text(encoding='utf-8-sig'))
        gb = _json.loads((FRAMES / 'busy3.json').read_text(encoding='utf-8-sig'))
        check('空闲时头顶无角标', gi['badge'][2] == 0 and gi['badge'][3] == 0,
              f"badge={gi['badge']}")
        check('有任务时显示数字', gb['badge'][2] > 10, f"badge={gb['badge']}")

        al = a[:, :, 3]
        rows = [y for y in range(al.shape[0]) if (al[y] > 200).sum() > 3]
        last_cat = max(rows)
        below = al[last_cat + 1:]
        check('阴影紧贴脚底', below.size > 0 and below.max() > 15,
              f'脚底行={last_cat}, 下方 alpha={int(below.max()) if below.size else 0}')

        l1 = Image.open(FRAMES / 'busy1-list.png')
        check('列表高度随会话数自适应', l1.size[1] < lst.size[1],
              f'1 条={l1.size[1]}px, 3 条={lst.size[1]}px')

        # 列表与猫之间不应有大片空白
        la = np.array(lst)
        lal = la[:, :, 3]
        rows_c = [(y, int((lal[y] > 25).sum())) for y in range(la.shape[0])]
        first = next((y for y, c in rows_c if c > 6), 0)
        last = la.shape[0] - 1 - next((y for y, c in reversed(rows_c) if c > 6), 0)
        gaps = run = 0
        for y in range(first, last + 1):
            if rows_c[y][1] <= 6:
                run += 1
            else:
                if run > 10:
                    gaps += 1
                run = 0
        check('列表与猫之间无大空隙', gaps == 0, f'空隙段={gaps}')

    # ---- 真实窗口样式 ----
    kill()
    time.sleep(1)
    subprocess.Popen([str(EXE), '--size', '140'])
    time.sleep(3)
    f = find()
    check('窗口已创建', f is not None, f[1] if f else 'none')
    if f:
        hwnd, box = f
        W, H = box[2] - box[0], box[3] - box[1]
        check('窗口尺寸合理', 60 <= W <= 400 and 60 <= H <= 400, f'{W}x{H}')
        ex = user32.GetWindowLongW(hwnd, -20)
        check('WS_EX_LAYERED（分层透明）', bool(ex & 0x00080000), f'0x{ex:08X}')
        check('WS_EX_TOOLWINDOW（不占任务栏）', bool(ex & 0x00000080), '')
    kill()

    # ---- 三档尺寸：角标17 + 间距2 + 身高 + 阴影6% ----
    ok_sizes = True
    detail = []
    for s in (72, 104, 180):
        kill()
        time.sleep(0.8)
        subprocess.Popen([str(EXE), '--size', str(s)])
        time.sleep(2.2)
        ff = find()
        if not ff:
            ok_sizes = False
            detail.append(f'{s}->none')
            continue
        hh = ff[1][3] - ff[1][1]
        want = 17 + 2 + s + max(6, int(s * 0.06))
        detail.append(f'{s}->{hh}/{want}')
        if abs(hh - want) > 2:
            ok_sizes = False
    check('三档尺寸都能应用', ok_sizes, ' '.join(detail))
    kill()

    counts = dsh_counts()
    check('能读到 DSH 会话缓存', counts is not None,
          f'{counts[1]} 个对话，{counts[0]} 个进行中' if counts else 'cache missing')

    passed = sum(1 for c in checks if c['ok'])
    report = {'passed': passed, 'failed': len(checks) - passed, 'checks': checks}
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / 'selftest-report.json').write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f"\n[selftest] {passed}/{len(checks)} passed")
    return 0 if passed == len(checks) else 1


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    finally:
        kill()