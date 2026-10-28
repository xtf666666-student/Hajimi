"""Verify the reply box through its own self-test (WinExe: redirect stdout to a file)."""
import os
import pathlib
import subprocess
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / 'packages/native/bin/Release/net48/HajimiPet.exe'

results = []


def check(name, ok, detail=''):
    results.append((bool(ok), name, str(detail)))
    print(f"[{'PASS' if ok else 'FAIL'}] {name} - {detail}")


def kill():
    subprocess.run(['powershell.exe', '-NoProfile', '-Command',
                    "Get-Process -Name HajimiPet -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }"],
                   capture_output=True)


def run_selftest(flag):
    """WinExe has no console, so capture via a redirected stdout file."""
    out_file = pathlib.Path(tempfile.gettempdir()) / 'rb-out.txt'
    err_file = pathlib.Path(tempfile.gettempdir()) / 'rb-err.txt'
    for f in (out_file, err_file):
        if f.exists():
            f.unlink()

    ps = (f"$p = Start-Process -FilePath '{EXE}' -ArgumentList '{flag}' -PassThru "
          f"-RedirectStandardOutput '{out_file}' -RedirectStandardError '{err_file}' -NoNewWindow; "
          f"$p.WaitForExit(60000) | Out-Null; \"exit=$($p.ExitCode)\"")
    res = subprocess.run(['powershell.exe', '-NoProfile', '-Command', ps],
                         capture_output=True, text=True)
    out = out_file.read_text(encoding='utf-8', errors='replace') if out_file.exists() else ''
    return out, res.stdout.strip()


def main():
    kill()
    time.sleep(1)

    out, exit_info = run_selftest('--reply-selftest')
    print('reply selftest:', out.strip()[:200], '|', exit_info)

    check('回复输入框可创建', 'box-ok' in out, '')
    check('输入框能拿到键盘焦点', 'focus-ok' in out, '')
    check('提交回调携带用户文本', 'submit-ok' in out, '')

    # 桥接自检（也验证输入框之后能真的发出去）
    out2, exit2 = run_selftest('--bridge-selftest')
    print('bridge selftest:', out2.strip()[:200], '|', exit2)
    check('桥接仍连通', 'ping-ok' in out2, '')

    kill()
    passed = sum(1 for ok, _, _ in results if ok)
    print(f'\n{passed}/{len(results)} passed')
    return 0 if passed == len(results) else 1


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    finally:
        kill()