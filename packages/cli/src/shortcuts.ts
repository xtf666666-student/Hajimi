import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname } from 'node:path';

function psString(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
}

/**
 * 用系统自带的 WScript.Shell 创建 .lnk 快捷方式（不下载、不执行任何外部脚本）。
 */
export function createShortcut(lnkPath: string, target: string, args = '', description = ''): void {
  mkdirSync(dirname(lnkPath), { recursive: true });
  const script = [
    "$ws = New-Object -ComObject WScript.Shell",
    `$s = $ws.CreateShortcut(${psString(lnkPath)})`,
    `$s.TargetPath = ${psString(target)}`,
    `$s.WorkingDirectory = ${psString(dirname(target))}`,
    `$s.Description = ${psString(description)}`,
    args ? `$s.Arguments = ${psString(args)}` : '',
    '$s.Save()',
  ]
    .filter(Boolean)
    .join('; ');
  execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    stdio: 'ignore',
    windowsHide: true,
  });
  if (!existsSync(lnkPath)) throw new Error(`快捷方式创建失败：${lnkPath}`);
}

export function removeShortcut(lnkPath: string): void {
  if (existsSync(lnkPath)) rmSync(lnkPath, { force: true });
}
