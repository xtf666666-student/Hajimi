"""End-to-end: does the pet exe actually reach the bridge and get sane answers?

The bridge port follows DSH_WEB_URL (DSH picks a new port on each launch),
so read it from the environment instead of hardcoding one.
"""
import json
import os
import pathlib
import re
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'
HOME = pathlib.Path(os.environ.get('DSH_HOME') or (pathlib.Path.home() / '.dsh'))
TOKEN = HOME / 'pet-bridge.token'

results = []


def check(name, ok, detail=''):
    results.append((bool(ok), name, str(detail)))
    print(f"[{'PASS' if ok else 'FAIL'}] {name} - {detail}")


def web_base():
    """DSH_WEB_URL if present; otherwise probe the common ports."""
    url = os.environ.get('DSH_WEB_URL')
    if url:
        return url.rstrip('/')
    # 端口每次启动都可能变，问一下 DSH 进程实际监听哪个
    ps = ("Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue | "
          "ForEach-Object { $_.Id } | ForEach-Object { "
          "Get-NetTCPConnection -State Listen -OwningProcess $_ -ErrorAction SilentlyContinue } | "
          "Where-Object { $_.LocalAddress -eq '127.0.0.1' } | "
          "Select-Object -ExpandProperty LocalPort -Unique")
    out = subprocess.run(['powershell.exe', '-NoProfile', '-Command', ps],
                         capture_output=True, text=True)
    ports = [int(p) for p in out.stdout.split() if p.strip().isdigit()]
    return ports


def main():
    print('token:', TOKEN, TOKEN.exists())
    if not TOKEN.exists():
        check('桥密钥文件存在', False, 'missing')
        return 1

    tok = TOKEN.read_text(encoding='utf-8').strip()
    base = web_base()
    print('DSH web base:', base)

    candidates = [base] if isinstance(base, str) else [f'http://127.0.0.1:{p}' for p in base]
    found = None
    for b in candidates:
        out = subprocess.run(
            ['curl.exe', '-s', '--max-time', '5',
             '-H', f'Authorization: Bearer {tok}', f'{b}/pet-bridge/ping'],
            capture_output=True)
        body = out.stdout.decode('utf-8', 'replace')
        if '"ok":true' in body.replace(' ', ''):
            found = (b, body)
            break

    check('桥接服务在线', found is not None,
          (found[1].strip()[:70] if found else 'not reachable on any port'))

    # 桌宠进程自己走一遍 PetBridge（含端口探测）
    proc = subprocess.run([str(EXE), '--bridge-selftest'], capture_output=True, timeout=60)
    stdout = proc.stdout.decode('utf-8', 'replace')
    print('pet selftest stdout:', stdout.strip()[:300])

    check('桌宠读取密钥成功', 'token-ok' in stdout, '')
    check('桌宠能连上桥接', 'ping-ok' in stdout, '')
    check('取消不存在会话返回可识别结果',
          'cancel-409' in stdout or 'cancel-ok' in stdout, '')

    passed = sum(1 for ok, _, _ in results if ok)
    print(f'\n{passed}/{len(results)} passed')
    return 0 if passed == len(results) else 1


if __name__ == '__main__':
    raise SystemExit(main())