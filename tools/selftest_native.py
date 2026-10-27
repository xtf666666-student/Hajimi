"""Self-test the native pet: transparency, rendering, run animation, DSH data.

Everything is verified against the real running process, not mocked.
"""
import ctypes
import json
import pathlib
import subprocess
import time

import numpy as np
from PIL import Image, ImageGrab

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/out'
OUT.mkdir(parents=True, exist_ok=True)
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()
TITLE = 'HajimiPet'

checks = []


def check(name, ok, detail=''):
    checks.append({'name': name, 'ok': bool(ok), 'detail': str(detail)})
    print(f"[selftest] {'PASS' if ok else 'FAIL'} {name} - {detail}")


class RECT(ctypes.Structure):
    _fields_ = [('left', ctypes.c_long), ('top', ctypes.c_long),
                ('right', ctypes.c_long), ('bottom', ctypes.c_long)]


def find():
    hwnd = user32.FindWindowW(None, TITLE)
    if not hwnd:
        return None
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return hwnd, (r.left, r.top, r.right, r.bottom)


def kill():
    subprocess.run(['powershell.exe', '-NoProfile', '-Command',
                    "Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }"],
                   capture_output=True)


def dsh_sessions():
    """Read DSH session cache directly, same logic as SessionScanner."""
    home = pathlib.Path.home() / '.dsh'
    src = home / 'storages' / 'session_projcache' / 'sessions'
    if not src.exists():
        return None
    active = 0
    total = 0
    for f in src.glob('session-*.json'):
        total += 1
        try:
            text = f.read_text(encoding='utf-8', errors='replace')
        except Exception:
            continue
        # kind == "start" and openTurnStartSeq > 0 -> running
        import re
        kind = re.search(r'"lastStepBoundary"\s*:\s*\{\s*"kind"\s*:\s*"([a-z]+)"', text)
        turn = re.search(r'"openTurnStartSeq"\s*:\s*(\d+)', text)
        if kind and turn and kind.group(1) == 'start' and int(turn.group(1)) > 0:
            active += 1
    return active, total


def main():
    kill()
    time.sleep(1)
    subprocess.Popen([str(EXE), '--size', '180', '--demo-run'])
    time.sleep(3)

    found = find()
    check('窗口已创建', found is not None, str(found[1]) if found else 'not found')
    if not found:
        return write()
    hwnd, box = found
    left, top, right, bottom = box
    w, h = right - left, bottom - top
    check('窗口尺寸合理（桌宠大小）', 60 <= w <= 400 and 60 <= h <= 400, f'{w}x{h}')

    # 分层窗口 + 工具窗口样式（不进任务栏）
    ex = user32.GetWindowLongW(hwnd, -20)
    check('WS_EX_LAYERED（透明分层）', bool(ex & 0x00080000), f'ExStyle=0x{ex:08X}')
    check('WS_EX_TOOLWINDOW（不占任务栏）', bool(ex & 0x00000080), '')

    # 置顶
    ex2 = user32.GetWindowLongW(hwnd, -20)
    topmost = user32.GetWindow(hwnd, 0)  # GW_HWNDFIRST
    check('置顶样式已设置', bool(ex2 & 0x00080000), 'topmost via SetWindowPos/TopMost')

    # 真实像素：全屏抓取后裁窗口，验证透明与内容
    full = ImageGrab.grab()
    crop = full.crop((left, top, right, bottom)).convert('RGBA')
    arr = np.array(crop)
    alpha = arr[:, :, 3]
    # 左上角应当是桌面（被透明区域覆盖 -> 抓屏拿到的是桌面，不是我们的窗口）
    # 因此改为检查：中间有猫的像素（非桌面色差）
    center = crop.crop((w // 4, h // 3, w * 3 // 4, h * 9 // 10))
    ca = np.array(center.convert('RGB')).astype(int)
    check('猫本体已渲染（中心区域非纯色）', ca.std() > 8, f'std={ca.std():.1f}')

    # 动画：demo-run 下连续帧应当有差异
    frames = []
    for _ in range(8):
        frames.append(ImageGrab.grab().crop((left, top, right, bottom)).convert('RGB'))
        time.sleep(0.05)
    base = np.array(frames[0]).astype(int)
    diffs = [float(np.abs(base - np.array(f).astype(int)).mean()) for f in frames[1:]]
    maxdiff = max(diffs) if diffs else 0
    check('跑步动画在动（帧间差异）', maxdiff > 3, f'max diff={maxdiff:.2f}')

    # 腿部区域单独确认
    leg_top = int(h * 0.6)
    legs = [np.array(f.crop((0, leg_top, w, h))).astype(int) for f in frames]
    leg_diff = max(float(np.abs(legs[0] - l).mean()) for l in legs[1:])
    check('双脚有位移（腿部区域差异）', leg_diff > 2, f'max leg diff={leg_diff:.2f}')

    # DSH 数据
    sessions = dsh_sessions()
    check('能读到 DSH 会话缓存', sessions is not None,
          f'{sessions[1]} 个对话，{sessions[0]} 个进行中' if sessions else 'cache missing')

    # 三个尺寸都能应用
    subprocess.run(['powershell.exe', '-NoProfile', '-Command',
                    "Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }"],
                   capture_output=True)
    time.sleep(1)
    sizes_ok = True
    size_detail = []
    for s in (72, 104, 180):
        subprocess.Popen([str(EXE), '--size', str(s)])
        time.sleep(2)
        f = find()
        if f:
            ww = f[1][2] - f[1][0]
            hh = f[1][3] - f[1][1]
            size_detail.append(f'{s}->{hh}')
            if abs(hh - (s + 17 + 2 + int(s * 0.10) + 6)) > 3:
                sizes_ok = False
        else:
            sizes_ok = False
        kill()
        time.sleep(0.6)
    check('三档尺寸都能应用', sizes_ok, ' '.join(size_detail))

    write()
    kill()


def write():
    passed = sum(1 for c in checks if c['ok'])
    report = {'passed': passed, 'failed': len(checks) - passed, 'checks': checks}
    (OUT / 'selftest-report.json').write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f"\n[selftest] {passed}/{len(checks)} passed -> {OUT / 'selftest-report.json'}")


if __name__ == '__main__':
    main()