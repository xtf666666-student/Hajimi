"""Capture the real pet at a large size so the leg motion can be judged."""
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
SIZE = 180


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
                      '--size', str(SIZE)])
    time.sleep(3)

    box = find()
    if not box:
        print('window not found')
        return
    left, top, right, bottom = box
    w, h = right - left, bottom - top
    print('pet rect', box, 'size', (w, h))

    user32.SetCursorPos((left + right) // 2, top + int(h * 0.78))
    time.sleep(1.5)

    frames = []
    for i in range(8):
        full = ImageGrab.grab()
        frames.append(full.crop((left, top, right, bottom)))
        time.sleep(0.06)

    Z = 2
    sheet = Image.new('RGB', (w * len(frames) + 6 * (len(frames) - 1), h), (247, 248, 250))
    for i, f in enumerate(frames):
        sheet.paste(f.convert('RGB'), (i * (w + 6), 0))
    sheet = sheet.resize((sheet.width * Z, sheet.height * Z), Image.LANCZOS)
    sheet.save(OUT / 'big-run-cycle.png')
    print('saved big-run-cycle.png', sheet.size)

    a = np.array(frames[0].convert('RGB')).astype(int)
    b = np.array(frames[3].convert('RGB')).astype(int)
    print('diff 0-3 =', round(np.abs(a - b).mean(), 2))


if __name__ == '__main__':
    main()