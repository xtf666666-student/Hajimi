"""Verify the speech bubble never overlaps the pet or the task list.

The bug: Say() is called outside Tick, so the old before/after visibility
comparison in Tick never fired and the window width never grew. The bubble was
then drawn inside the old width and covered the pet.
"""
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'
OUT = ROOT / 'tools/out/bubble'


def rects_overlap(a, b):
    ax, ay, aw, ah = a
    bx, by, bw, bh = b
    if aw <= 0 or ah <= 0 or bw <= 0 or bh <= 0:
        return False
    return not (ax + aw <= bx or bx + bw <= ax or ay + ah <= by or by + bh <= ay)


for d in (OUT,):
    if d.exists():
        for f in d.iterdir():
            f.unlink()
    d.mkdir(parents=True, exist_ok=True)

out_file = ROOT / 'tools/out/bubble-log.txt'
with open(out_file, 'w') as o:
    subprocess.run([str(EXE), '--size', '140', '--dump', str(OUT)],
                   stdout=o, stderr=subprocess.STDOUT, timeout=120)

frames = ['bubble.png']
checks = []

for name in frames:
    j = OUT / name.replace('.png', '.json')
    if not j.exists():
        print(f'{name}: 缺少 json')
        continue
    g = json.loads(j.read_text(encoding='utf-8-sig'))
    win = g['window']
    cat = g['cat']
    bubble = g['bubble']
    lst = g['list']

    print(f'--- {name}')
    print(f'  窗口   {win}')
    print(f'  猫     {cat}')
    print(f'  气泡   {bubble}')
    print(f'  列表   {lst}')

    hit_cat = rects_overlap(bubble, cat)
    hit_list = rects_overlap(bubble, lst)
    inside = (bubble[0] >= 0 and bubble[1] >= 0 and
              bubble[0] + bubble[2] <= win[0] and
              bubble[1] + bubble[3] <= win[1])

    checks.append(('气泡不压桌宠', not hit_cat))
    checks.append(('气泡不压任务列表', not hit_list))
    checks.append(('气泡完整在窗口内', inside))

    # 水平间距
    gap = cat[0] - (bubble[0] + bubble[2])
    print(f'  气泡右缘到猫左缘间距 = {gap}px')

print()
ok = True
for label, passed in checks:
    print(f'{"PASS" if passed else "FAIL"}  {label}')
    if not passed:
        ok = False

raise SystemExit(0 if ok else 1)