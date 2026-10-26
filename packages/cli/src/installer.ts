import { spawnSync } from 'node:child_process';
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
} from 'node:fs';
import { basename, join } from 'node:path';
import { download, looksLikeWindowsBinary, sha256File } from './download';
import {
  desktopDir,
  downloadDir,
  installRoot,
  petsDir,
  platformKey,
  startMenuPrograms,
} from './paths';
import { findPet, loadRegistry, resolveAsset } from './registry';
import { createShortcut, removeShortcut } from './shortcuts';
import { loadState, saveState } from './state';
import type { InstalledPet, InstalledVersion } from './types';
import { color, detail, error as logError, formatBytes, info, success, warn } from './ui';

export interface InstallOptions {
  petId: string;
  release?: string;
  kind: 'portable' | 'installer';
  desktopShortcut: boolean;
  noShortcut: boolean;
  force: boolean;
  yes: boolean;
  verbose: boolean;
}

const START_MENU_LINK = () => join(startMenuPrograms(), '哈基米桌宠.lnk');
const DESKTOP_LINK = () => join(desktopDir(), '哈基米桌宠.lnk');

function requireWindows(): void {
  if (process.platform !== 'win32') {
    throw new Error(`当前脚本只能在 Windows 上安装桌宠（检测到 ${process.platform}）`);
  }
}

export async function install(options: InstallOptions): Promise<number> {
  requireWindows();
  const { registry, source } = await loadRegistry(options.verbose);
  const pet = findPet(registry, options.petId);
  const asset = resolveAsset(pet, options.release, options.kind);

  if (options.verbose) {
    detail(`注册表来源：${source}`);
    detail(`目标平台：${platformKey()}`);
    detail(`安装包：${asset.url}`);
    if (asset.sha256) detail(`SHA-256：${asset.sha256}`);
  }

  const state = loadState();
  const installed = state.pets[pet.id];
  const sameVersion = installed?.installs.find((item) => item.version === asset.version);
  if (sameVersion && !options.force) {
    info(`已经安装 ${pet.name} ${asset.version}（${sameVersion.path}）`);
    info('如需重新安装，加 --force');
    await ensureShortcuts(pet.id, pet.name, sameVersion.path, options);
    return 0;
  }

  const file = join(downloadDir(), asset.fileName);
  await download(asset.url, file, {
    sha256: asset.sha256,
    verbose: options.verbose,
  });

  if (!looksLikeWindowsBinary(file)) {
    throw new Error('下载的文件不是有效的 Windows 可执行文件（缺少 MZ 头）');
  }
  const actualSha = sha256File(file);
  detail(`SHA-256 校验通过：${actualSha}`);

  if (asset.kind === 'installer' && options.kind === 'installer') {
    return runInstaller(file, pet.id, pet.name, asset.version, options);
  }

  const targetDir = join(petsDir(), pet.id, asset.version);
  mkdirSync(targetDir, { recursive: true });
  const exePath = join(targetDir, 'HajimiDesktopPet.exe');
  copyFileSync(file, exePath);
  rmSync(file, { force: true });

  upsertInstall(state, pet.id, pet.name, {
    version: asset.version,
    path: exePath,
    installedAt: new Date().toISOString(),
    kind: 'portable',
    sha256: actualSha,
  });
  state.pets[pet.id].currentVersion = asset.version;
  saveState(state);

  // 安装新版本后清理旧版本目录（保留当前版本）
  pruneOldVersions(pet.id, asset.version);

  const shortcuts = await ensureShortcuts(pet.id, pet.name, exePath, options);
  success(`${pet.name} ${asset.version} 安装完成`);
  detail(`安装位置：${targetDir}`);
  detail(`体积：${formatBytes(readSize(exePath))}`);
  for (const link of shortcuts) detail(`快捷方式：${link}`);
  info('从开始菜单搜索「哈基米桌宠」即可启动；也可以直接运行上面的 exe。');
  return 0;
}

async function ensureShortcuts(
  petId: string,
  name: string,
  exePath: string,
  options: InstallOptions,
): Promise<string[]> {
  if (options.noShortcut || process.platform !== 'win32') return [];
  const created: string[] = [];
  try {
    const startLink = START_MENU_LINK();
    createShortcut(startLink, exePath, '', name);
    created.push(startLink);
    if (options.desktopShortcut) {
      const desktopLink = DESKTOP_LINK();
      createShortcut(desktopLink, exePath, '', name);
      created.push(desktopLink);
    }
    const state = loadState();
    if (state.pets[petId]) {
      state.pets[petId].shortcuts = created;
      saveState(state);
    }
  } catch (error) {
    warn(`快捷方式创建失败：${String(error)}（exe 仍然可用）`);
  }
  return created;
}

function readSize(file: string): number {
  try {
    return statSync(file).size;
  } catch {
    return 0;
  }
}

function upsertInstall(
  state: ReturnType<typeof loadState>,
  petId: string,
  name: string,
  version: InstalledVersion,
): void {
  const existing: InstalledPet =
    state.pets[petId] ?? { id: petId, name, currentVersion: version.version, installs: [], shortcuts: [] };
  existing.name = name;
  existing.installs = existing.installs.filter((item) => item.version !== version.version);
  existing.installs.push(version);
  state.pets[petId] = existing;
}

function pruneOldVersions(petId: string, keepVersion: string): void {
  const dir = join(petsDir(), petId);
  if (!existsSync(dir)) return;
  for (const entry of readdirSync(dir)) {
    if (entry !== keepVersion) {
      rmSync(join(dir, entry), { recursive: true, force: true });
    }
  }
  const state = loadState();
  if (state.pets[petId]) {
    state.pets[petId].installs = state.pets[petId].installs.filter((item) => item.version === keepVersion);
    saveState(state);
  }
}

function runInstaller(
  file: string,
  petId: string,
  name: string,
  version: string,
  options: InstallOptions,
): number {
  if (!options.yes) {
    if (!process.stdin.isTTY) {
      logError('NSIS 安装包需要确认，当前不是交互终端。请去掉 --installer 使用便携版，或加 --yes。');
      return 1;
    }
    process.stdout.write(`即将运行官方安装包 ${basename(file)}（会打开安装向导），继续？[y/N] `);
    const answer = readFileSync(0, 'utf8').trim().toLowerCase();
    if (answer !== 'y' && answer !== 'yes') {
      info('已取消安装');
      return 1;
    }
  }
  info(`运行安装包：${file}`);
  const result = spawnSync(file, [], { stdio: 'inherit' });
  if (result.status !== 0) {
    logError(`安装包退出码 ${result.status ?? '未知'}`);
    return 1;
  }
  const state = loadState();
  upsertInstall(state, petId, name, {
    version,
    path: file,
    installedAt: new Date().toISOString(),
    kind: 'installer',
  });
  state.pets[petId].currentVersion = version;
  saveState(state);
  success(`${name} ${version} 已通过官方安装包安装`);
  return 0;
}

export async function update(petId: string, options: Omit<InstallOptions, 'petId' | 'release' | 'kind'>): Promise<number> {
  requireWindows();
  const state = loadState();
  const installed = state.pets[petId];
  if (!installed) {
    warn(`尚未安装「${petId}」，直接执行安装`);
    return install({ ...options, petId, kind: 'portable' });
  }
  const { registry } = await loadRegistry(options.verbose);
  const pet = findPet(registry, petId);
  const latest = pet.latest;
  if (installed.currentVersion === latest) {
    success(`已是最新版本 ${latest}`);
    return 0;
  }
  info(`发现新版本：${installed.currentVersion} → ${latest}`);
  return install({ ...options, petId, kind: 'portable' });
}

export async function uninstall(petId: string): Promise<number> {
  const state = loadState();
  const installed = state.pets[petId];
  if (!installed) {
    warn(`未安装「${petId}」，无需卸载`);
    return 0;
  }
  for (const link of installed.shortcuts) removeShortcut(link);
  // 兼容旧状态里没记录快捷方式的情况
  if (installed.shortcuts.length === 0) {
    removeShortcut(START_MENU_LINK());
    removeShortcut(DESKTOP_LINK());
  }
  const dir = join(petsDir(), petId);
  rmSync(dir, { recursive: true, force: true });
  delete state.pets[petId];
  saveState(state);
  success(`已卸载 ${installed.name}`);
  detail(`移除目录：${dir}`);
  info(`本地配置仍在：${join(installRoot(), 'state.json')}（仅保留安装记录）`);
  return 0;
}

export function list(verbose = false): number {
  const state = loadState();
  const pets = Object.values(state.pets);
  if (pets.length === 0) {
    info('还没有安装任何桌宠。');
    detail(`安装根目录：${installRoot()}`);
    info('安装命令：npx hajimi-pet-cli install hajimi-desktop-pet');
    return 0;
  }
  console.log(`${color.bold('已安装桌宠')}（${pets.length}）`);
  for (const pet of pets) {
    const install = pet.installs.find((item) => item.version === pet.currentVersion) ?? pet.installs[0];
    console.log(`\n  ${color.green('●')} ${color.bold(pet.name)} ${color.dim(`(${pet.id})`)}`);
    console.log(`    版本：${pet.currentVersion || '未知'}`);
    if (install) {
      console.log(`    位置：${install.path}`);
      console.log(`    安装时间：${install.installedAt}`);
    }
    if (pet.shortcuts.length > 0) console.log(`    快捷方式：${pet.shortcuts.join(' | ')}`);
    if (verbose && install?.sha256) console.log(`    SHA-256：${install.sha256}`);
  }
  console.log('');
  return 0;
}

export async function checkUpdate(petId: string, verbose = false): Promise<number> {
  const state = loadState();
  const installed = state.pets[petId];
  if (!installed) {
    warn(`未安装「${petId}」`);
    return 1;
  }
  const { registry } = await loadRegistry(verbose);
  const latest = findPet(registry, petId).latest;
  if (latest === installed.currentVersion) {
    success(`${installed.name} 已是最新版本 ${latest}`);
    return 0;
  }
  info(`${installed.name} 有新版本：${installed.currentVersion} → ${latest}`);
  detail(`更新命令：npx hajimi-pet-cli update ${petId}`);
  return 0;
}

export function shellHint(): void {
  const state = loadState();
  for (const pet of Object.values(state.pets)) {
    const install = pet.installs.find((item) => item.version === pet.currentVersion);
    if (install) console.log(`${pet.id}\t${pet.currentVersion}\t${install.path}`);
  }
}

export function ensureInstallRoot(): void {
  mkdirSync(installRoot(), { recursive: true });
}
