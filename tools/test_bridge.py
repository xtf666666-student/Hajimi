"""Test the dsh-pet-bridge plugin end to end.

Tries to reach the bridge on the DSH web port using the shared token file.
DSH must have been restarted after the plugin was installed.
"""
import json
import os
import pathlib
import subprocess

HOME = pathlib.Path(os.environ.get('DSH_HOME') or (pathlib.Path.home() / '.dsh'))
TOKEN_FILE = HOME / 'pet-bridge.token'
PORT = 61922

results = []


def check(name, ok, detail=''):
    results.append((bool(ok), name, str(detail)))
    print(f"[{'PASS' if ok else 'FAIL'}] {name} - {detail}")


def call(method, path, body=None):
    args = ['curl.exe', '-s', '-w', '\n%{http_code}', '--max-time', '8',
            '-X', method, f'http://127.0.0.1:{PORT}/pet-bridge{path}']
    if TOKEN_FILE.exists():
        token = TOKEN_FILE.read_text(encoding='utf-8').strip()
        args += ['-H', f'Authorization: Bearer {token}']
    if body is not None:
        args += ['-H', 'Content-Type: application/json', '--data-binary', json.dumps(body)]
    out = subprocess.run(args, capture_output=True)
    text = out.stdout.decode('utf-8', 'replace').strip()
    parts = text.rsplit('\n', 1)
    payload = parts[0] if len(parts) == 2 else text
    code = parts[-1] if parts and parts[-1].isdigit() else '000'
    return code, payload


def main():
    print('token file:', TOKEN_FILE)
    check('桥密钥文件已生成', TOKEN_FILE.exists(),
          f'{TOKEN_FILE.stat().st_size} bytes' if TOKEN_FILE.exists() else 'missing')

    code, body = call('GET', '/ping')
    print(f'  GET /ping -> {code} {body[:200]}')
    check('GET /ping 可用（插件已挂载）', code == '200' and '"ok":true' in body.replace(' ', ''),
          f'HTTP {code}')

    # 无密钥必须被拒
    out = subprocess.run(['curl.exe', '-s', '-o', 'NUL', '-w', '%{http_code}', '--max-time', '8',
                          f'http://127.0.0.1:{PORT}/pet-bridge/ping'],
                         capture_output=True, text=True)
    check('无密钥请求被拒绝', out.stdout.strip() == '401', f'HTTP {out.stdout.strip()}')

    # 错误密钥也必须被拒
    out = subprocess.run(['curl.exe', '-s', '-o', 'NUL', '-w', '%{http_code}', '--max-time', '8',
                          '-H', 'Authorization: Bearer wrong-token-000000000000000000000',
                          f'http://127.0.0.1:{PORT}/pet-bridge/ping'],
                         capture_output=True, text=True)
    check('错误密钥被拒绝', out.stdout.strip() == '401', f'HTTP {out.stdout.strip()}')

    # cancel 一个不存在的会话：应返回 409 而不是崩
    code, body = call('POST', '/cancel', {'sessionId': 'session-does-not-exist-0000'})
    print(f'  POST /cancel(bogus) -> {code} {body[:200]}')
    check('取消不存在的会话不会崩', code in ('409', '404'), f'HTTP {code}')

    # 缺 sessionId：400
    code, body = call('POST', '/cancel', {})
    check('缺 sessionId 返回 400', code == '400', f'HTTP {code}')

    passed = sum(1 for ok, _, _ in results if ok)
    print(f'\n{passed}/{len(results)} passed')
    return 0 if passed == len(results) else 1


if __name__ == '__main__':
    raise SystemExit(main())