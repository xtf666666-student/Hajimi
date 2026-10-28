"""Find DSH keyboard shortcuts for stop/cancel and for focusing the composer."""
import io
import pathlib
import re

ASAR = r'D:\soft\deepseek-harness\files\resources\app.asar'
data = open(ASAR, 'rb').read()
out = io.StringIO()


def w(*p):
    out.write(' '.join(str(x) for x in p) + '\n')


w('=== keybinding entries mentioning stop/cancel/abort ===')
pats = [rb'"?(?:key|keys|accelerator|hotkey)"?\s*:\s*"[^"]{1,40}"']
for m in re.finditer(rb'\{[^{}]{0,160}(?:stop|cancel|abort|interrupt)[^{}]{0,160}\}', data):
    s = m.group(0).decode('utf-8', 'replace')
    if 'Key' in s or 'Escape' in s or 'key' in s.lower():
        w('  ', s[:200])
    if len(out.getvalue()) > 6000:
        break

w('\n=== Escape / Esc handling near cancel ===')
for m in list(re.finditer(rb'Escape', data))[:12]:
    i = m.start()
    ctx = data[max(0, i - 200):i + 200].decode('utf-8', 'replace').replace('\n', ' ')
    if any(k in ctx for k in ('cancel', 'stop', 'abort', 'interrupt', 'close')):
        w('  ', ctx[:380])
        w('  ---')

w('\n=== stop button action id candidates ===')
for pat in (rb'stopTurn', rb'"stop"', rb'cancelTurn', rb'cancel-turn', rb'stop-turn',
            rb'onStop', rb'handleStop'):
    n = data.count(pat)
    if n:
        i = data.find(pat)
        w(f'  {pat.decode()} x{n}: ',
          data[max(0, i - 160):i + 200].decode('utf-8', 'replace').replace('\n', ' ')[:340])

w('\n=== composer focus / Ctrl+K style shortcuts ===')
for m in re.finditer(rb'"[a-zA-Z]*[Kk]ey[a-zA-Z]*"\s*:\s*\{[^{}]{0,120}\}', data):
    s = m.group(0).decode('utf-8', 'replace')
    if any(k in s.lower() for k in ('escape', 'mod+k', 'cmd+k', 'ctrl+k', 'enter')):
        w('  ', s[:180])

pathlib.Path(r'D:\soft\deepseek-harness\harness-pet\tools\out').mkdir(parents=True, exist_ok=True)
pathlib.Path(r'D:\soft\deepseek-harness\harness-pet\tools\out\shortcuts.txt').write_text(
    out.getvalue(), encoding='utf-8')
print('written, bytes:', len(out.getvalue()))