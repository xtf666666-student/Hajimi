import { homedir } from 'node:os';
import { join, resolve } from 'node:path';

/** 安装根目录，可用 HAJIMI_HOME 覆盖（测试与自定义安装位置用）。 */
export function installRoot(): string {
  if (process.env.HAJIMI_HOME) return resolve(process.env.HAJIMI_HOME);
  const local = process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local');
  return join(local, 'HajimiPet');
}

export function petsDir(): string {
  return join(installRoot(), 'pets');
}

export function stateFile(): string {
  return join(installRoot(), 'state.json');
}

export function downloadDir(): string {
  return join(installRoot(), 'downloads');
}

export function startMenuPrograms(): string {
  const appData = process.env.APPDATA ?? join(homedir(), 'AppData', 'Roaming');
  return join(appData, 'Microsoft', 'Windows', 'Start Menu', 'Programs');
}

export function desktopDir(): string {
  return join(process.env.USERPROFILE ?? homedir(), 'Desktop');
}

export function platformKey(): string {
  const arch = process.arch === 'arm64' ? 'arm64' : process.arch === 'ia32' ? 'ia32' : 'x64';
  return `${process.platform}-${arch}`;
}
