"""Verify completed cards persist until explicitly closed."""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages' / 'native' / 'bin' / 'Release' / 'net48' / 'HajimiPet.exe'

tmp = pathlib.Path(tempfile.mkdtemp(prefix='lc3-'))
cache = tmp / 'storages' / 'session_projcache' / 'sessions'
cache.mkdir(parents=True)


def mk(idx, title, kind, seq, age, resp='', touch_now=False):
    payload = {
        'version': 7,
        'record': {
            'identity': {'cwd': 'D:\\x'},
            'rows': {
                'title': {'seq': 1, 'val': title},
                'turnBoundary': {'seq': 1, 'val': {
                    'openTurnStartSeq': seq,
                    'lastTurn': 5,
                    'lastStepBoundary': {'kind': kind, 'seq': 1},
                }},
                'turnOutline': {'seq': 1, 'val': {'turns': [
                    {'turn': 5, 'seq': 5, 'prompt': 'x', 'response': resp}]}},
                'sessionListMetadata': {'seq': 1, 'val': {
                    'lastPromptAt': int(time.time() * 1000) - int(age * 1000),
                    'blank': False}},
                'sessionStats': {'seq': 1, 'val': {
                    'turns': 5, 'lastTurn': 5, 'openStep': None, 'pendingCalls': {}}},
            },
        },
    }
    p = cache / ('session-test-0000-0000-0000-0000000000%02d.json' % idx)
    p.write_text(json.dumps(payload, ensure_ascii=False), encoding='utf-8')
    ts = time.time() - (0 if touch_now else age)
    os.utime(p, (ts, ts))
    return p


mk(1, '正在跑的任务', 'start', 100, 3, '运行中输出')
mk(2, '刚完成的任务', 'end', None, 600, '已完成但保留')
mk(3, '用户已从DSH打开的', 'end', None, 600, '应被条件3关闭', touch_now=True)

env = dict(os.environ)
env['DSH_HOME'] = str(tmp)

out = tmp / 'o.txt'
with open(out, 'w') as o:
    subprocess.run([str(EXE), '--size', '140', '--dump', str(tmp / 'frames')],
                   env=env, stdout=o, stderr=subprocess.STDOUT, timeout=120)

print('dump 输出:', out.read_text(encoding='utf-8', errors='replace')[:200])

gj = tmp / 'frames' / 'real.json'
if not gj.exists():
    print('FAIL: 无 real.json')
    shutil.rmtree(tmp, ignore_errors=True)
    raise SystemExit(1)

g = json.loads(gj.read_text(encoding='utf-8-sig'))
rows = g['rows'][0]
print(f'卡片数 = {rows}   期望 2')
ok = rows == 2
print('PASS' if ok else 'FAIL')

shutil.rmtree(tmp, ignore_errors=True)
raise SystemExit(0 if ok else 1)
