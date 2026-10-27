"""Capture the pet in --demo-run mode so the run cycle can be inspected.

The cursor-simulation API does not take effect in this session, so we force the
run state instead; hover-triggered running is exercised separately in the app.
"""
import ctypes
import pathlib
import subprocess
import time

import numpy as np
from PIL import Image, ImageGrab

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/out'
OUT.mkdir(parents=True, exist_ok=True)

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()
TITLE = 'HajimiPet'


class RECT(ctypes.Structure):
    _fields_ = [('left', ctypes.c_long), ('top', ctypes.c_long),
                ('right', ctypes.c_long), ('bottom', ctypes.c_long)]


def find():
    hwnd = user32.FindWindowW(None, TITLE)
    if not hwnd:
        return None
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def main():
    subprocess.run(['powershell.exe', '-NoProfile', '-Command',
                    "Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }"],
                   capture_output=True)
    time.sleep(1)
    subprocess.Popen([str(ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'),
                      '--size', '180', '--demo-run'])
    time.sleep(3)

    box = find()
    if not box:
        print('window not found')
        return
    left, top, right, bottom = box
    w, h = right - left, bottom - top
    print('rect', box, 'size', (w, h))

    frames = []
    for i in range(8):
        full = ImageGrab.grab()
        frames.append(full.crop((left, top, right, bottom)).convert('RGB'))
        time.sleep(0.05)          # 50ms ≈ 1.5 ticks

    a = np.array(frames[0]).astype(int)
    diffs = [round(float(np.abs(a - np.array(f).astype(int)).mean()), 2) for f in frames[1:]]
    print('diffs vs frame0:', diffs)

    Z = 2
    sheet = Image.new('RGB', (w * len(frames) + 6 * (len(frames) - 1), h), (247, 248, 250))
    for i, f in enumerate(frames):
        sheet.paste(f, (i * (w + 6), 0))
    sheet = sheet.resize((int(sheet.width * Z), int(sheet.height * Z)), Image.LANCZOS)
    sheet.save(OUT / 'demo-run.png')
    print('saved demo-run.png', sheet.size)

    # 只裁腿部，放大看细节
    top_leg = int(h * 0.60)
    crops = [f.crop((0, top_leg, w, h)) for f in frames]
    cw, ch = crops[0].size
    Z2 = 4
    leg = Image.new('RGB', (cw * len(crops) + 4 * (len(crops) - 1), ch), (255, 255, 255))
    for i, c in enumerate(crops):
        leg.paste(c, (i * (cw + 4), 0))
    leg = leg.resize((int(leg.width * Z2), int(leg.height * Z2)), Image.NEAREST)
    leg.save(OUT / 'demo-legs.png')
    print('saved demo-legs.png', leg.size)


if __name__ == '__main__':
    main()