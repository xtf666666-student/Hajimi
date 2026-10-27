"""Long-run memory sample: 5 minutes, one sample every 30s, single process lifetime."""
import json
import subprocess
import time

EXE = r'D:\soft\deepseek-harness\harness-pet\packages\app\release\win-unpacked\HajimiDshPet.exe'
ROOT = r'D:\soft\deepseek-harness\harness-pet\packages\app'


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


def main():
    ps("Get-Process -Name HajimiDshPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }")
    time.sleep(2)
    subprocess.Popen([EXE], cwd=ROOT)
    rows = []
    for step in range(11):
        time.sleep(30 if step else 8)
        data = sample()
        if not data:
            print(f'sample {step}: 进程已退出')
            continue
        ws = round(sum(r['WS'] for r in data), 1)
        pv = round(sum(r['Priv'] for r in data), 1)
        gpu = next((r['WS'] for r in data if r['Role'] == 'gpu'), 0)
        rows.append((ws, pv))
        print(f'{step*30 if step else 8:>4}s  WS={ws:>6}MB  Private={pv:>6}MB  GPU={gpu}MB  ({len(data)} 进程)')
    if rows:
        wss = sorted(r[0] for r in rows)
        pvs = sorted(r[1] for r in rows)
        print(f'\nWorkingSet 范围: {wss[0]} ~ {wss[-1]} MB, 中位 {wss[len(wss)//2]} MB')
        print(f'Private    范围: {pvs[0]} ~ {pvs[-1]} MB, 中位 {pvs[len(pvs)//2]} MB')
    ps("Get-Process -Name HajimiDshPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }")


if __name__ == '__main__':
    main()
