"""Launch the packaged pet, sample memory at several points, then close it.

Everything happens inside one process lifetime so the parent shell cannot reap
the child between samples.
"""
import json
import subprocess
import time

EXE = r'packages\app\release\win-unpacked\HajimiDshPet.exe'
ROOT = r'D:\soft\deepseek-harness\harness-pet'


def ps(cmd):
    out = subprocess.run(
        ['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', cmd],
        capture_output=True, text=True, encoding='utf-8', errors='replace',
    )
    return out.stdout.strip()


def sample():
    script = (
        "Get-CimInstance Win32_Process -Filter \"name='HajimiDshPet.exe'\" | ForEach-Object { "
        "$role = if ($_.CommandLine -match '--type=renderer') { 'renderer' } "
        "elseif ($_.CommandLine -match '--type=gpu') { 'gpu' } "
        "elseif ($_.CommandLine -match '--type=utility') { 'utility' } else { 'main' }; "
        "$p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue; "
        "if ($p) { [pscustomobject]@{ Role=$role; WS=[math]::Round($p.WorkingSet64/1MB,1); "
        "Priv=[math]::Round($p.PrivateMemorySize64/1MB,1) } } } | ConvertTo-Json -Compress"
    )
    raw = ps(script)
    if not raw:
        return []
    data = json.loads(raw)
    return [data] if isinstance(data, dict) else data


def report(label, rows):
    if not rows:
        print(f'{label}: 无进程')
        return
    ws = sum(r['WS'] for r in rows)
    pv = sum(r['Priv'] for r in rows)
    detail = '  '.join(f"{r['Role']}={r['WS']}" for r in sorted(rows, key=lambda r: -r['WS']))
    print(f'{label}  WS合计={round(ws,1)}MB  Private合计={round(pv,1)}MB  [{detail}]')


def main():
    ps(f"Get-Process -Name HajimiDshPet -ErrorAction SilentlyContinue | ForEach-Object {{ $_.Kill() }}")
    time.sleep(2)
    subprocess.Popen([EXE], cwd=f'{ROOT}\\packages\\app')
    timeline = [('T+3s 启动中', 3), ('T+15s', 12), ('T+45s 稳定', 30), ('T+90s 空闲', 45)]
    for label, wait in timeline:
        time.sleep(wait)
        report(label, sample())
    ps("Get-Process -Name HajimiDshPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }")


if __name__ == '__main__':
    main()
