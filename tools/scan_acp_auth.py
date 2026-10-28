"""Find how the ACP endpoint authenticates and which port it binds."""
import io
import pathlib
import re

ASAR = r'D:\soft\deepseek-harness\files\resources\app.asar'
data = open(ASAR, 'rb').read()
out = io.StringIO()


def w(*p):
    out.write(' '.join(str(x) for x in p) + '\n')


w('=== isAuthorized implementation ===')
for m in list(re.finditer(rb'function isAuthorized', data))[:3]:
    i = m.start()
    w(data[i:i + 700].decode('utf-8', 'replace').replace('\n', ' '))
    w('---')

w('\n=== ACP server startup (port + token) ===')
for m in list(re.finditer(rb'ACP HTTP endpoint listening', data))[:2]:
    i = m.start()
    w(data[max(0, i - 900):i + 200].decode('utf-8', 'replace').replace('\n', ' ')[:1100])
    w('---')

w('\n=== acpPort / ACP_PORT constants ===')
for m in re.finditer(rb'[A-Za-z_]*[Aa][Cc][Pp][A-Za-z_]*[Pp]ort[A-Za-z_]*\s*=\s*[^,;\n]{0,60}', data):
    w('  ', m.group(0).decode('utf-8', 'replace'))
for m in re.finditer(rb'process\.env\.(DSH_[A-Z_]*ACP[A-Z_]*)', data):
    w('  env:', m.group(1).decode())
for m in re.finditer(rb'DSH_ACP[A-Z_]*', data):
    w('  const:', m.group(0).decode())

w('\n=== who starts the ACP http server (context) ===')
for pat in (rb'startAcpServer', rb'acpServer', rb'serveAcp', rb'acpHttp'):
    n = data.count(pat)
    if n:
        i = data.find(pat)
        w(f'  {pat.decode()} x{n}')
        w('     ', data[max(0, i - 300):i + 400].decode('utf-8', 'replace').replace('\n', ' ')[:700])

pathlib.Path(r'D:\soft\deepseek-harness\harness-pet\tools\out').mkdir(parents=True, exist_ok=True)
pathlib.Path(r'D:\soft\deepseek-harness\harness-pet\tools\out\acp-auth.txt').write_text(
    out.getvalue(), encoding='utf-8')
print('written')