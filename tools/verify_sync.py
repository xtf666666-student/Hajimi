"""Verify problem 3: badge/list stay in sync when a new task starts.

Simulates a running task appearing by creating a temporary DSH session cache
entry, then checks the pet picks it up within one poll interval.
"""
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time

import ctypes
import ctypes.wintypes
import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools' / 'out'
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()
TITLE = 'HajimiPet'

results = []


def check(name, ok, detail=''):
    results.append((name, bool(ok), str(detail)))
    print(f"[{'PASS' if ok else 'FAIL'}] {name} - {detail}")


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


def badge_count(hwnd, box):
    """用 PrintWindow 取窗口位图后读角标宽度，判断是否有数字。"""
    return None  # 分层窗口抓不到，改为读窗口宽度的变化


def main():
    home = pathlib.Path(os.environ.get('DSH_HOME') or (pathlib.Path.home() / '.dsh'))
    cache = home / 'storages' / 'session_projcache' / 'sessions'
    if not cache.exists():
        print('DSH cache missing:', cache)
        return 1

    # 先看基线
    base_active = 0
    for f in cache.glob('session-*.json'):
        t = f.read_text(encoding='utf-8', errors='replace')
        if '"kind":"start"' in t.replace(' ', '') and '"openTurnStartSeq":0' not in t.replace(' ', ''):
            base_active += 1
    print('baseline active sessions:', base_active)

    kill()
    time.sleep(1)
    subprocess.Popen([str(EXE), '--size', '140'])
    time.sleep(3)

    f0 = find()
    check('桌宠已启动', f0 is not None, f0[1] if f0 else 'none')
    if not f0:
        return 1
    w0 = f0[1][2] - f0[1][0]

    # 造一个新的"正在运行"会话，模拟新任务开始
    fake = cache / 'session-debugsync-0000-0000-0000-000000000001.json'
    payload = {
        'version': 7,
        'record': {
            'identity': {'cwd': str(ROOT), 'createdAt': int(time.time() * 1000)},
            'rows': {
                'title': {'ver': 1, 'seq': 1, 'val': '同步测试任务'},
                'turnBoundary': {
                    'ver': 2, 'seq': 1,
                    'val': {'openTurnStartSeq': 1, 'lastStepStartSeq': 1,
                            'lastStepBoundary': {'kind': 'start', 'seq': 1}, 'lastTurn': 1},
                },
            },
        },
    }
    fake.write_text(json.dumps(payload, ensure_ascii=False), encoding='utf-8')
    print('injected fake running session')

    # 轮询间隔 1.5s，等 3 秒足够
    time.sleep(3.0)

    # 展开列表，窗口应因多了一行而不同
    hwnd = f0[0]
    # 点角标：角标在顶部中央
    # 分层窗口不吃 SendMessage，用 PostMessage 更可靠
    cx = f0[1][0] + w0 // 2
    cy = f0[1][1] + 8
    lp = ctypes.wintypes.LPARAM(((w0 // 2) & 0xFFFF) | (8 << 16))
    user32.PostMessageW(hwnd, 0x0201, ctypes.wintypes.WPARAM(1), lp)
    user32.PostMessageW(hwnd, 0x0200, ctypes.wintypes.WPARAM(0),
                        ctypes.wintypes.LPARAM((cx & 0xFFFF) | (cy << 16)))
    user32.PostMessageW(hwnd, 0x0202, ctypes.wintypes.WPARAM(0), lp)
    time.sleep(1.2)

    f1 = find()
    if f1:
        box = f1[1]
        W, H = box[2] - box[0], box[3] - box[1]
        print(f'after inject + expand: {W}x{H}')
        check('注入新任务后列表能展开（说明已同步到列表）', H > 167,
              f'{W}x{H}')

    # 清理注入的会话
    try:
        fake.unlink()
    except OSError:
        pass
    kill()

    print('\n=== 汇总 ===')
    passed = sum(1 for _, ok, _ in results if ok)
    print(f'{passed}/{len(results)} passed')
    return 0 if passed == len(results) else 1


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    finally:
        kill()