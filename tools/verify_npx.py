"""Verify the real `npx <tarball> install` path end to end."""
import ctypes
import json
import os
import pathlib
import shutil
import subprocess
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
CLI = ROOT / 'packages' / 'cli'
TGZ = next(CLI.glob('hajimi-pet-cli-*.tgz'))

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()
TITLE = 'HajimiPet'


class RECT(ctypes.Structure):
    _fields_ = [('left', ctypes.c_long), ('top', ctypes.c_long),
                ('right', ctypes.c_long), ('bottom', ctypes.c_long)]


def kill():
    subprocess.run(['powershell.exe', '-NoProfile', '-Command',
                    "Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }"],
                   capture_output=True)


def main():
    kill()
    root = pathlib.Path(os.environ.get('LOCALAPPDATA', '')) / 'HajimiPet'
    shutil.rmtree(root, ignore_errors=True)

    print(f'=== npx {TGZ.name} install ===')
    res = subprocess.run(
        ['npx', '--yes', str(TGZ), 'install', '--no-shortcut'],
        capture_output=True, text=True, encoding='utf-8', errors='replace',
        shell=False,
    )
    print(res.stdout.strip())
    if res.returncode != 0:
        print('STDERR:', (res.stderr or '')[-2000:])
        return 1

    state_file = root / 'state.json'
    if not state_file.exists():
        print('FAIL: state.json missing')
        return 1
    state = json.loads(state_file.read_text(encoding='utf-8'))
    exe = pathlib.Path(state['pets']['hajimi-desktop-pet']['path'])
    print(f'\nexe: {exe}')
    print(f'exists: {exe.exists()}  size: {exe.stat().st_size/1024:.1f} KB')

    print('\n=== launch ===')
    subprocess.Popen([str(exe)])
    time.sleep(4)
    hwnd = user32.FindWindowW(None, TITLE)
    if hwnd:
        r = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        ex = user32.GetWindowLongW(hwnd, -20)
        print(f'window: {(r.left, r.top)} size={r.right-r.left}x{r.bottom-r.top}')
        print(f'layered={bool(ex & 0x00080000)} toolwindow={bool(ex & 0x00000080)}')
    else:
        print('FAIL: window not found')

    ps = ("Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | "
          "Select-Object -First 1 -Property WorkingSet64 | ConvertTo-Json -Compress")
    out = subprocess.run(['powershell.exe', '-NoProfile', '-Command', ps],
                         capture_output=True, text=True).stdout.strip()
    if out:
        print(f"WorkingSet: {json.loads(out)['WorkingSet64']/1024/1024:.1f} MB")

    time.sleep(1)
    kill()

    print('\n=== npx uninstall ===')
    res = subprocess.run(['npx', '--yes', str(TGZ), 'uninstall'],
                         capture_output=True, text=True, encoding='utf-8', errors='replace')
    print(res.stdout.strip())
    print('cleaned:', not (root / 'hajimi-desktop-pet').exists())
    shutil.rmtree(root, ignore_errors=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())