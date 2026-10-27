"""Sample the packaged pet's memory footprint over time, per process role."""
import json
import subprocess
import time

EXE = 'HajimiDshPet.exe'


def ps(cmd):
    out = subprocess.run(
        ['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', cmd],
        capture_output=True, text=True, encoding='utf-8', errors='replace',
    )
    return out.stdout.strip()


def sample():
    script = (
        "Get-CimInstance Win32_Process -Filter \"name='%s'\" | ForEach-Object { "
        "$role = if ($_.CommandLine -match '--type=renderer') { 'renderer' } "
        "elseif ($_.CommandLine -match '--type=gpu') { 'gpu' } "
        "elseif ($_.CommandLine -match '--type=utility') { 'utility' } else { 'main' }; "
        "$p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue; "
        "if ($p) { [pscustomobject]@{ Role=$role; WS=[math]::Round($p.WorkingSet64/1MB,1); "
        "Priv=[math]::Round($p.PrivateMemorySize64/1MB,1) } } } | ConvertTo-Json -Compress" % EXE
    )
    raw = ps(script)
    if not raw:
        return []
    data = json.loads(raw)
    if isinstance(data, dict):
        data = [data]
    return data


def report(label, rows):
    if not rows:
        print(f'{label}: 无进程')
        return
    ws = sum(r['WS'] for r in rows)
    pv = sum(r['Priv'] for r in rows)
    parts = ', '.join(f"{r['Role']}={r['WS']}" for r in sorted(rows, key=lambda r: -r['WS']))
    print(f'{label}: {len(rows)} 进程 | WorkingSet 合计 {round(ws,1)} MB | Private 合计 {round(pv,1)} MB')
    print(f'    明细(MB): {parts}')


if __name__ == '__main__':
    for label in ['T+0s（刚启动）', 'T+15s', 'T+60s', 'T+120s']:
        if label != 'T+0s（刚启动）':
            time.sleep(15 if label == 'T+15s' else 45 if label == 'T+60s' else 60)
        report(label, sample())
