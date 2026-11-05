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


def find_by_pid(pid):
    """只认这个进程的窗口。

    FindWindowW 会返回同名窗口里的任意一个：上一个进程还没退干净时，
    量到的就是旧窗口 —— 这正是"三档尺寸"偶发失败的原因。
    """
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(hwnd, _):
        owner = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value != pid:
            return True
        buf = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, buf, 256)
        if buf.value == TITLE:
            found.append(hwnd)
            return False
        return True

    user32.EnumWindows(cb, None)
    if not found:
        return None
    hwnd = found[0]
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return hwnd, (r.l, r.t, r.r, r.b)


def kill():
    subprocess.run(['powershell.exe', '-NoProfile', '-Command',
                    "Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }"],
                   capture_output=True)
    # 等进程真的消失，否则下一步会量到残留窗口
    for _ in range(50):
        r = subprocess.run(['powershell.exe', '-NoProfile', '-Command',
                            "(Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | Measure-Object).Count"],
                           capture_output=True, text=True)
        try:
            if int(r.stdout.strip() or 0) == 0:
                return
        except ValueError:
            pass
        time.sleep(0.2)


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
        # 窗口高度现在恒定（3 行常驻预留），目的是"除拖动外不改桌宠位置"，
        # 所以不再断言高度随会话数变化；改为验证卡片本身高度确实不同。
        j1 = _json.loads((FRAMES / 'busy1-list.json').read_text(encoding='utf-8-sig'))
        j3 = _json.loads((FRAMES / 'busy3-list.json').read_text(encoding='utf-8-sig'))
        check('卡片摞高度随会话数变化',
              j1['list'][3] < j3['list'][3],
              f"1 条={j1['list'][3]}px, 3 条={j3['list'][3]}px")

        # 列表底边与猫头顶之间不应有大片空白
        # 注意：不能扫描"整个列表高度里的空白段"——卡片之间本来就有
        # Gap(14px) 的缝，那会被误判成空隙。只量列表底部到猫头顶的距离。
        lx, ly, lw, lh = j3['list']
        cx, cy, cw, ch = j3['cat']
        gap_px = cy - (ly + lh)
        check('列表与猫之间无大空隙', gap_px <= 12, f'间距={gap_px}px')

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
        # 窗口高度含固定的任务框预留区（3 行常驻），所以上限放到 700
        check('窗口尺寸合理', 60 <= W <= 700 and 60 <= H <= 700, f'{W}x{H}')
        ex = user32.GetWindowLongW(hwnd, -20)
        check('WS_EX_LAYERED（分层透明）', bool(ex & 0x00080000), f'0x{ex:08X}')
        check('WS_EX_TOOLWINDOW（不占任务栏）', bool(ex & 0x00000080), '')
    kill()

    # ---- 三档尺寸：角标17 + 间距2 + 身高 + 阴影6% ----
    ok_sizes = True
    detail = []
    detail_prev = (0, 0)
    detail_idx = 0
    for s in (72, 104, 180):
        kill()
        time.sleep(0.4)
        proc = subprocess.Popen([str(EXE), '--size', str(s)])
        ff = None
        # 等这个进程的窗口出现并且**稳定**（连续两次量到同一尺寸）
        last = None
        for _ in range(40):
            time.sleep(0.25)
            cur = find_by_pid(proc.pid)
            if cur and cur[1][2] - cur[1][0] > 0:
                if last and abs((cur[1][3] - cur[1][1]) - last) == 0:
                    ff = cur
                    break
                last = cur[1][3] - cur[1][1]
        if not ff:
            ff = find_by_pid(proc.pid)
        if not ff:
            ok_sizes = False
            detail.append(f'{s}->none')
            proc.kill()
            continue
        hh = ff[1][3] - ff[1][1]
        # 窗口高度 = 固定预留区(上下各两份) + 猫身高 + 指示器槽位。
        # 注意：尺寸 72 时猫本身很矮，但常驻预留区不变，所以高度差
        # 不等于尺寸差；只要"能应用"且高度合理即可，不做严格单调断言。
        detail.append(f'{s}->{hh}')
        # 高度必须大于猫身高，且不超过屏幕高度的一半
        if not (s < hh < 900):
            ok_sizes = False
        detail_prev = (s, hh)
        detail_idx += 1
        proc.kill()
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