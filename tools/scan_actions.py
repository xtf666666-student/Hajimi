"""Check whether DSH exposes a way to (a) send a message to a session, (b) cancel it.

Writes findings to tools/out/actions-scan.txt to avoid console encoding issues.
"""
import io
import pathlib
import re

ASAR = r'D:\soft\deepseek-harness\files\resources\app.asar'
data = open(ASAR, 'rb').read()
out = io.StringIO()


def w(*p):
    out.write(' '.join(str(x) for x in p) + '\n')


w('=== 1. agent.session.input.* events ===')
for ev in (b'agent.session.input.message', b'agent.session.input.cancel',
           b'agent.session.input.tool_result'):
    for m in list(re.finditer(re.escape(ev), data))[:2]:
        i = m.start()
        w(f'--- {ev.decode()}')
        w('   ', data[max(0, i - 320):i + 320].decode('utf-8', 'replace').replace('\n', ' ')[:640])

w('\n=== 2. RPC method names for prompt / cancel ===')
names = set()
for m in re.finditer(rb'["\']((?:session|turn|agent)[./][a-zA-Z.]{2,40})["\']', data):
    v = m.group(1).decode('utf-8', 'replace')
    if any(k in v for k in ('cancel', 'prompt', 'interrupt', 'abort', 'stop')):
        names.add(v)
for v in sorted(names)[:40]:
    w('  ', v)

w('\n=== 3. any cancel/abort RPC in the ACP client ===')
for pat in (rb'session/cancel', rb'session.cancel', rb'turn/cancel', rb'turn.cancel',
            rb'acp/cancel', rb'cancelTurn', rb'abortTurn', rb'interrupt'):
    n = data.count(pat)
    if n:
        i = data.find(pat)
        w(f'  {pat.decode()} x{n}')
        w('     ', data[max(0, i - 220):i + 260].decode('utf-8', 'replace').replace('\n', ' ')[:460])

w('\n=== 4. how the GUI stops a running turn (UI action id) ===')
for pat in (rb'stop-generating', rb'stopGenerating', rb'cancel-turn', rb'abort-turn',
            rb'ui-stop', rb'interrupt-turn'):
    n = data.count(pat)
    if n:
        i = data.find(pat)
        w(f'  {pat.decode()} x{n}')
        w('     ', data[max(0, i - 260):i + 300].decode('utf-8', 'replace').replace('\n', ' ')[:520])

w('\n=== 5. session cache: is there a pending-user-input queue we could write to? ===')
for pat in (rb'next-turn', rb'next-step', rb'"inbox"'):
    n = data.count(pat)
    w(f'  {pat.decode()} x{n}')

pathlib.Path(r'D:\soft\deepseek-harness\harness-pet\tools\out').mkdir(parents=True, exist_ok=True)
pathlib.Path(r'D:\soft\deepseek-harness\harness-pet\tools\out\actions-scan.txt').write_text(
    out.getvalue(), encoding='utf-8')
print('written')