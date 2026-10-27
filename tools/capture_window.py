"""Capture a layered window's actual content via PrintWindow(PW_RENDERFULLCONTENT).

Screen grabs miss WS_EX_LAYERED windows that use UpdateLayeredWindow on some
compositing paths, so capture the window directly instead.
"""
import ctypes
import ctypes.wintypes
import pathlib
import sys

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools' / 'out'
OUT.mkdir(parents=True, exist_ok=True)

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
user32.SetProcessDPIAware()


class RECT(ctypes.Structure):
    _fields_ = [('left', ctypes.c_long), ('top', ctypes.c_long),
                ('right', ctypes.c_long), ('bottom', ctypes.c_long)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [('biSize', ctypes.c_uint32), ('biWidth', ctypes.c_long),
                ('biHeight', ctypes.c_long), ('biPlanes', ctypes.c_ushort),
                ('biBitCount', ctypes.c_ushort), ('biCompression', ctypes.c_uint32),
                ('biSizeImage', ctypes.c_uint32), ('biXPelsPerMeter', ctypes.c_long),
                ('biYPelsPerMeter', ctypes.c_long), ('biClrUsed', ctypes.c_uint32),
                ('biClrImportant', ctypes.c_uint32)]


def capture(hwnd, title='HajimiPet'):
    if not hwnd:
        hwnd = user32.FindWindowW(None, title)
    if not hwnd:
        return None
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    if w <= 0 or h <= 0:
        return None

    hdc_screen = user32.GetDC(0)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    bmi = BITMAPINFOHEADER()
    bmi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.biWidth = w
    bmi.biHeight = -h          # top-down
    bmi.biPlanes = 1
    bmi.biBitCount = 32
    bmi.biCompression = 0      # BI_RGB

    bits = ctypes.c_void_p()
    hbmp = gdi32.CreateDIBSection(hdc_screen, ctypes.byref(bmi), 0,
                                  ctypes.byref(bits), None, 0)
    old = gdi32.SelectObject(hdc_mem, hbmp)

    # PW_RENDERFULLCONTENT = 2 —— 这个标志才能真正拿到分层窗口内容
    ok = user32.PrintWindow(hwnd, hdc_mem, 2)

    buf = ctypes.string_at(bits, w * h * 4)
    gdi32.SelectObject(hdc_mem, old)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(0, hdc_screen)

    img = Image.frombuffer('RGBA', (w, h), buf, 'raw', 'BGRA', 0, 1)
    return img, bool(ok)


def main():
    img, ok = capture(0)
    if img is None:
        print('window not found')
        return 1
    name = sys.argv[1] if len(sys.argv) > 1 else 'capture'
    img.save(OUT / f'{name}.png')
    print(f'PrintWindow ok={ok} size={img.size} -> {name}.png')

    import numpy as np
    a = np.array(img)
    print('alpha max:', a[:, :, 3].max(), 'opaque px:', int((a[:, :, 3] > 200).sum()))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())