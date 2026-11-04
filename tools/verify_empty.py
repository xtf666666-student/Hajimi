"""Prove that with zero running sessions the pet shows nothing at all."""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'

tmp = pathlib.Path(tempfile.mkdtemp(prefix='dsh-empty-'))
cache = tmp / 'storages' / 'session_projcache' / 'sessions'
cache.mkdir(parents=True)

# 造 3 个已结束的会话（kind=end），全部有 title
for i in range(3):
    payload = {
        'version': 7,
        'record': {
            'identity': {'cwd': 'D:\\x'},
            'rows': {
                'title': {'seq': 1, 'val': f'已结束的会话 {i + 1}'},
                'turnBoundary': {
                    'seq': 1,
                    'val': {'openTurnStartSeq': None, 'lastTurn': i + 1,
                            'lastStepBoundary': {'kind': 'end', 'seq': 1}},
                },
                'sessionListMetadata': {'seq': 1, 'val': {'lastPromptAt': 1700000000000}},
            },
        },
    }
    (cache / f'session-test-0000-0000-0000-00000000000{i}.json').write_text(
        json.dumps(payload, ensure_ascii=False), encoding='utf-8')

print('temp DSH_HOME:', tmp)
env = dict(os.environ)
env['DSH_HOME'] = str(tmp)

# WinExe 没有控制台，必须重定向到文件才能拿到输出
out_file = tmp / 'out.txt'
err_file = tmp / 'err.txt'
with open(out_file, 'w') as o, open(err_file, 'w') as e:
    proc = subprocess.run(
        [str(EXE), '--size', '140', '--dump', str(tmp / 'frames')],
        env=env, stdout=o, stderr=e, timeout=120)
print('exit:', proc.returncode)
log = out_file.read_text(encoding='utf-8', errors='replace')
print('dump log:', log.strip()[:300])
err = err_file.read_text(encoding='utf-8', errors='replace')
if err.strip():
    print('stderr:', err.strip()[:300])

gj = tmp / 'frames' / 'real.json'
if not gj.exists():
    print('FAIL: real.json 未生成')
    shutil.rmtree(tmp, ignore_errors=True)
    raise SystemExit(1)

g = json.loads(gj.read_text(encoding='utf-8-sig'))
rows = g['rows'][0]
badge = g['badge']
win = g['window']

print()
print(f'窗口      : {win[0]}x{win[1]}')
print(f'rows      : {rows}   (期望 0)')
print(f'badge     : {badge}  (期望全 0)')

ok = rows == 0 and badge[2] == 0 and badge[3] == 0
print()
print('PASS' if ok else 'FAIL', '- 零任务时完全不显示' if ok else '- 仍有内容被显示')

# 顺便确认窗口没有为角标/列表预留高度
if ok:
    base = 17 + 2 + 140 + max(6, int(140 * 0.06))
    print(f'窗口高度 {win[1]} vs 纯猫身 {base}（无角标应更矮）')

shutil.rmtree(tmp, ignore_errors=True)
raise SystemExit(0 if ok else 1)
