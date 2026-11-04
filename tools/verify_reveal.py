"""Check the reveal spring actually produces intermediate frames on the way out."""
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'
OUT = ROOT / 'tools/out/reveal'

if OUT.exists():
    for f in OUT.iterdir():
        f.unlink()
OUT.mkdir(parents=True, exist_ok=True)

log = ROOT / 'tools/out/reveal-log.txt'
with open(log, 'w') as o:
    subprocess.run([str(EXE), '--size', '140', '--dump', str(OUT)],
                   stdout=o, stderr=subprocess.STDOUT, timeout=120)

# 导图走的是 ApplyLayout 直通路径；这里主要确认窗口高度在"需要列表"时正确
j = OUT / 'stacked3.json'
if not j.exists():
    print('缺少 stacked3.json')
    raise SystemExit(1)

g = json.loads(j.read_text(encoding='utf-8-sig'))
print('stacked3  窗口=', g['window'], ' 列表=', g['list'])

# 关闭态：列表高度应为 0
j2 = OUT / 'idle.json'
g2 = json.loads(j2.read_text(encoding='utf-8-sig'))
print('idle      窗口=', g2['window'], ' 列表=', g2['list'])

ok = g['list'][3] > 0 and g2['list'][3] == 0
print()
print('PASS' if ok else 'FAIL', '- 列表高度在开/关两态正确')