"""Regression test: a stale 'start' marker must not be reported as running.

Reproduces the bug the user hit: DSH had no running tasks, but the pet showed
one, because a session cache was left with lastStepBoundary.kind == 'start'
after an interrupted turn (its file had not changed in ~47 hours).
"""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'

tmp = pathlib.Path(tempfile.mkdtemp(prefix='dsh-zombie-'))
cache = tmp / 'storages' / 'session_projcache' / 'sessions'
cache.mkdir(parents=True)


def make_session(idx, title, kind, open_seq, prompt_ms, age_seconds):
    payload = {
        'version': 7,
        'record': {
            'identity': {'cwd': 'D:\\x'},
            'rows': {
                'title': {'seq': 1, 'val': title},
                'turnBoundary': {'seq': 1, 'val': {
                    'openTurnStartSeq': open_seq,
                    'lastTurn': 5,
                    'lastStepBoundary': {'kind': kind, 'seq': 1},
                }},
                'sessionListMetadata': {'seq': 1, 'val': {
                    'lastPromptAt': prompt_ms, 'blank': False}},
                'sessionStats': {'seq': 1, 'val': {
                    'turns': 5, 'lastTurn': 5, 'openStep': None,
                    'pendingCalls': {}}},
            },
        },
    }
    p = cache / f'session-test-0000-0000-0000-0000000000{idx:02d}.json'
    p.write_text(json.dumps(payload, ensure_ascii=False), encoding='utf-8')
    # 关键：把文件时间设成指定"年龄"
    ts = time.time() - age_seconds
    os.utime(p, (ts, ts))
    return p


now_ms = int(time.time() * 1000)

# 1) 僵尸：标记为 start，但 47 小时没动过
make_session(1, '僵尸会话-47小时前中断', 'start', 134,
             now_ms - 47 * 3600 * 1000, 47 * 3600)

# 2) 真正在跑：标记 start，文件刚写过
make_session(2, '真在跑的会话', 'start', 11433, now_ms - 5000, 5)

# 3) 正常结束
make_session(3, '已结束的会话', 'end', None,
             now_ms - 600 * 1000, 600)

print('temp DSH_HOME:', tmp)
print('构造: 1 个僵尸(start,47h前) + 1 个真在跑(start,5s前) + 1 个已结束')
print()

env = dict(os.environ)
env['DSH_HOME'] = str(tmp)

out_file = tmp / 'out.txt'
err_file = tmp / 'err.txt'
with open(out_file, 'w') as o, open(err_file, 'w') as e:
    proc = subprocess.run(
        [str(EXE), '--size', '140', '--dump', str(tmp / 'frames')],
        env=env, stdout=o, stderr=e, timeout=120)

gj = tmp / 'frames' / 'real.json'
if not gj.exists():
    print('FAIL: real.json 未生成')
    print(err_file.read_text(encoding='utf-8', errors='replace')[:400])
    shutil.rmtree(tmp, ignore_errors=True)
    raise SystemExit(1)

g = json.loads(gj.read_text(encoding='utf-8-sig'))
rows = g['rows'][0]
print(f'检测到的运行中任务数 = {rows}   (期望 1，只算真在跑的那个)')
print(f'badge = {g["badge"]}')

ok = (rows == 1)
print()
print('PASS' if ok else 'FAIL',
      '- 僵尸会话已被活性检查排除' if ok else '- 僵尸仍被当成运行中')

# 也确认列表里没有僵尸标题
if not ok:
    print('  说明：rows 应为 1；若为 2 说明只靠 kind=="start" 判断，活性检查没生效')

shutil.rmtree(tmp, ignore_errors=True)
raise SystemExit(0 if ok else 1)
