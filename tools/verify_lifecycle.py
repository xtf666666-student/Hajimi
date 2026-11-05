"""Verify: a fresh pet start shows no completed cards, only running ones."""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages' / 'native' / 'bin' / 'Release' / 'net48' / 'HajimiPet.exe'

tmp = pathlib.Path(tempfile.mkdtemp(prefix='lc6-'))
cache = tmp / 'storages' / 'session_projcache' / 'sessions'
cache.mkdir(parents=True)


def mk(idx, title, kind, seq, age, resp=''):
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
    ts = time.time() - age
    os.utime(p, (ts, ts))


env = dict(os.environ)
env['DSH_HOME'] = str(tmp)

# 构造：1 个运行中 + 5 个近期已完成（模拟"上次跑完后重启"）
mk(1, 'A-正在跑', 'start', 100, 5, '运行中')
for i in range(2, 7):
    mk(i, '已完成%d' % i, 'end', None, 60 * i, '这是一段结果文本')

subprocess.run([str(EXE), '--size', '140', '--dump', str(tmp / 'f1')],
               env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)

g = json.loads((tmp / 'f1' / 'real.json').read_text(encoding='utf-8-sig'))
rows = g['rows'][0]
print(f'全新启动（1 运行中 + 5 已完成）：卡片数 = {rows}   期望 1')
print()

ok = rows == 1
print('PASS' if ok else 'FAIL',
      '- 启动时只显示正在跑的，已完成的一律不显示' if ok else '')

shutil.rmtree(tmp, ignore_errors=True)
raise SystemExit(0 if ok else 1)
