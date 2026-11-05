"""Verify completed-card lifecycle: only recent completions appear."""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages' / 'native' / 'bin' / 'Release' / 'net48' / 'HajimiPet.exe'

tmp = pathlib.Path(tempfile.mkdtemp(prefix='lc4-'))
cache = tmp / 'storages' / 'session_projcache' / 'sessions'
cache.mkdir(parents=True)


def mk(idx, title, kind, seq, age_seconds, resp='', touch_now=False):
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
                    'lastPromptAt': int(time.time() * 1000) - int(age_seconds * 1000),
                    'blank': False}},
                'sessionStats': {'seq': 1, 'val': {
                    'turns': 5, 'lastTurn': 5, 'openStep': None, 'pendingCalls': {}}},
            },
        },
    }
    p = cache / ('session-test-0000-0000-0000-0000000000%02d.json' % idx)
    p.write_text(json.dumps(payload, ensure_ascii=False), encoding='utf-8')
    ts = time.time() - (0 if touch_now else age_seconds)
    os.utime(p, (ts, ts))
    return p


# 1) 运行中 -> 显示
mk(1, 'A-正在跑', 'start', 100, 35, '运行中')
# 2) 5 分钟前完成 -> 显示（刚完成，30 分钟内）
mk(2, 'B-刚完成5分钟', 'end', None, 300, '刚完成的结果')
# 3) 10 小时前完成 -> 隐藏（历史会话）
mk(3, 'C-十小时前完成', 'end', None, 36000, '历史结果')
# 4) 3 天前完成 -> 隐藏
mk(4, 'D-三天前完成', 'end', None, 259200, '更早的结果')
# 5) 刚完成但文件刚被写过 -> 隐藏（条件 3）
mk(5, 'E-刚被DSH打开', 'end', None, 300, '应被关闭', touch_now=True)
# 6) 又一个历史会话 -> 隐藏
mk(6, 'F-一天前完成', 'end', None, 86400, '历史')

env = dict(os.environ)
env['DSH_HOME'] = str(tmp)

out = tmp / 'o.txt'
with open(out, 'w') as o:
    subprocess.run([str(EXE), '--size', '140', '--dump', str(tmp / 'frames')],
                   env=env, stdout=o, stderr=subprocess.STDOUT, timeout=120)

gj = tmp / 'frames' / 'real.json'
if not gj.exists():
    print('FAIL: 无 real.json')
    shutil.rmtree(tmp, ignore_errors=True)
    raise SystemExit(1)

g = json.loads(gj.read_text(encoding='utf-8-sig'))
rows = g['rows'][0]

print(f'卡片数 = {rows}   期望 2（1 运行中 + 1 刚完成）')
print()
ok = rows == 2
print('PASS' if ok else 'FAIL')

shutil.rmtree(tmp, ignore_errors=True)
raise SystemExit(0 if ok else 1)
