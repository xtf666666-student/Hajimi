"""Probe the running DSH instance for an accessible ACP / RPC endpoint."""
import json
import pathlib
import socket
import subprocess

OUT = pathlib.Path(r'D:\soft\deepseek-harness\harness-pet\tools\out')
OUT.mkdir(parents=True, exist_ok=True)

results = []


def curl(url, method='GET', headers=None, body=None, timeout=6):
    args = ['curl.exe', '-s', '-o', 'NUL', '-w', '%{http_code}',
            '-X', method, '--max-time', str(timeout)]
    for h in (headers or []):
        args += ['-H', h]
    if body is not None:
        args += ['--data-binary', body]
    args.append(url)
    out = subprocess.run(args, capture_output=True, text=True)
    return out.stdout.strip()


def listening_ports():
    ps = ("Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | "
          "Where-Object { $_.LocalAddress -eq '127.0.0.1' } | "
          "Select-Object -ExpandProperty LocalPort | Sort-Object -Unique")
    out = subprocess.run(['powershell.exe', '-NoProfile', '-Command', ps],
                         capture_output=True, text=True)
    ports = []
    for line in out.stdout.splitlines():
        line = line.strip()
        if line.isdigit():
            ports.append(int(line))
    return ports


def main():
    ports = listening_ports()
    print('loopback listening ports:', ports)

    token = None
    # 从 DSH 进程环境/命令行找 token 线索
    ps = ("Get-CimInstance Win32_Process -Filter \"name='DeepSeek Harness.exe'\" | "
          "Select-Object -First 1 -ExpandProperty CommandLine")
    cmd = subprocess.run(['powershell.exe', '-NoProfile', '-Command', ps],
                         capture_output=True, text=True, errors='replace').stdout.strip()
    print('dsh cmdline head:', cmd[:160])

    for port in ports:
        base = f'http://127.0.0.1:{port}'
        # 只挑可能相关的端口
        code_root = curl(base + '/')
        code_acp = curl(base + '/acp')
        if code_root in ('200', '401', '302') or code_acp not in ('000', ''):
            results.append((port, code_root, code_acp))
            print(f'  port {port}: / -> {code_root}, /acp -> {code_acp}')

    (OUT / 'ports.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    print('\ncandidates:', results)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())