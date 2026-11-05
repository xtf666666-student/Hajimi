"""Verify only sessions the pet actually watched running produce cards."""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages' / 'native' / 'bin' / 'Release' / 'net48' / 'HajimiPet.exe'

tmp = pathlib.Path(tempfile.mkdtemp(prefix='lc5-'))
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


env = dict(os.environ)
env['DSH_HOME'] = str(tmp)

# ---- 第一轮：只有运行中的会话 ----
mk(1, 'A-正在跑', 'start', 100, 5, '运行中')
subprocess.run([str(EXE), '--size', '140', '--dump', str(tmp / 'f1')],
               env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)

g1 = json.loads((tmp / 'f1' / 'real.json').read_text(encoding='utf-8-sig'))
print('第一轮（只有运行中）：卡片数 =', g1['rows'][0], '  期望 1')

# 检查 seen 文件里是否记下了 ran=true
seen = tmp / 'hajimi-pet-seen.json'
has_ran = 'session-test-0000-0000-0000-000000000001' in seen.read_text(encoding='utf-8')
print('桌宠记录「见过它跑」=', has_ran)

# ---- 第二轮：把会话改成已完成，模拟"它跑完了" ----
mk(1, 'A-已跑完', 'end', None, 120, '刚完成的结果')
# 再加 4 个"从没被桌宠见过在跑"的历史已完成会话（还原用户截图里的情况）
for i in range(2, 6):
    mk(i, '历史%d' % i, 'end', None, 3600 * 24, '历史结果')

subprocess.run([str(EXE), '--size', '140', '--dump', str(tmp / 'f2')],
               env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)

g2 = json.loads((tmp / 'f2' / 'real.json').read_text(encoding='utf-8-sig'))
rows2 = g2['rows'][0]
print('第二轮（1 个跑完 + 4 个历史）：卡片数 =', rows2, '  期望 1')
print()

ok = g1['rows'][0] == 1 and rows2 == 1
print('PASS' if ok else 'FAIL',
      '- 只有桌宠亲眼见过的会话才出卡片')

shutil.rmtree(tmp, ignore_errors=True)
raise SystemExit(0 if ok else 1)
