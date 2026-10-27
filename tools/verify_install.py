"""真实用户视角验证：模拟 `npx hajimi-pet-cli install`，然后确认桌宠能启动。

用 HAJIMI_HOME 指向真正的默认位置（或临时目录），检查：
  1. 安装后 exe 落地
  2. 直接运行 exe，窗口出现且为分层透明窗口
  3. 内存占用
"""
import ctypes
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
CLI = ROOT / 'packages' / 'cli'

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


def find():
    hwnd = user32.FindWindowW(None, TITLE)
    if not hwnd:
        return None
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return hwnd, (r.left, r.top, r.right, r.bottom)


def main():
    kill()

    # 真实用户命令：npx hajimi-pet-cli install（这里直接用本地包等价调用）
    print('=== 执行安装命令 ===')
    res = subprocess.run(
        ['node', str(CLI / 'bin' / 'hajimi.js'), 'install', '--no-shortcut'],
        capture_output=True, text=True, encoding='utf-8', errors='replace',
        cwd=str(CLI.parent.parent),
    )
    print(res.stdout.strip())
    if res.returncode != 0:
        print('STDERR:', res.stderr)
        return 1

    # 从 state.json 找出安装位置
    state_file = pathlib.Path(os.environ.get('LOCALAPPDATA', '')) / 'HajimiPet' / 'state.json'
    if not state_file.exists():
        print('state.json 未生成:', state_file)
        return 1
    state = json.loads(state_file.read_text(encoding='utf-8'))
    exe = pathlib.Path(state['pets']['hajimi-desktop-pet']['path'])
    print('\n=== 安装结果 ===')
    print('exe:', exe, 'exists:', exe.exists(), f'{exe.stat().st_size/1024:.1f} KB')

    bundled = CLI / 'assets' / 'HajimiPet.exe'
    same = (hashlib.sha256(exe.read_bytes()).digest() ==
            hashlib.sha256(bundled.read_bytes()).digest())
    print('sha256 与包内一致:', same)

    # 启动安装后的 exe
    print('\n=== 启动安装后的桌宠 ===')
    proc = subprocess.Popen([str(exe)])
    time.sleep(4)

    found = find()
    print('窗口已出现:', found is not None)
    if found:
        hwnd, box = found
        w, h = box[2] - box[0], box[3] - box[1]
        ex = user32.GetWindowLongW(hwnd, -20)
        print(f'  位置={box[:2]} 尺寸={w}x{h}')
        print(f'  WS_EX_LAYERED  = {bool(ex & 0x00080000)}')
        print(f'  WS_EX_TOOLWINDOW = {bool(ex & 0x00000080)}')

    # 内存
    ps = ("Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | "
          "Select-Object -First 1 -Property Id,WorkingSet64,PrivateMemorySize64 | "
          "ConvertTo-Json -Compress")
    out = subprocess.run(['powershell.exe', '-NoProfile', '-Command', ps],
                         capture_output=True, text=True).stdout.strip()
    if out:
        info = json.loads(out)
        print(f"\n内存: WorkingSet={info['WorkingSet64']/1024/1024:.1f} MB, "
              f"Private={info['PrivateMemorySize64']/1024/1024:.1f} MB")

    time.sleep(2)
    kill()

    # 卸载
    print('\n=== 执行卸载命令 ===')
    res = subprocess.run(
        ['node', str(CLI / 'bin' / 'hajimi.js'), 'uninstall'],
        capture_output=True, text=True, encoding='utf-8', errors='replace',
    )
    print(res.stdout.strip())
    print('安装目录已清理:', not exe.parent.parent.exists())

    # 清掉测试留下的默认安装根
    shutil.rmtree(pathlib.Path(os.environ.get('LOCALAPPDATA', '')) / 'HajimiPet',
                  ignore_errors=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())