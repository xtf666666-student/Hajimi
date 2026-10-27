#!/usr/bin/env node
/**
 * hajimi-pet-cli —— 哈基米桌宠命令行安装器
 *
 *   npx hajimi-pet-cli install hajimi-desktop-pet
 *   npx hajimi-pet-cli list
 *   npx hajimi-pet-cli update hajimi-desktop-pet
 *   npx hajimi-pet-cli uninstall hajimi-desktop-pet
 *
 * 设计要点：
 *  - 桌宠本体是约 700KB 的原生 Windows exe（.NET Framework 4.8 分层窗口），
 *    直接随 npm 包分发，安装时复制到 %LOCALAPPDATA%\HajimiPet，不需要联网下载。
 *  - 不执行任何下载来的脚本：只做「复制文件 + 创建快捷方式」。
 */
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawnSync } = require('node:child_process');

const PKG = require('../package.json');
const REGISTRY = require('../pets.json');
const DEFAULT_PET = 'hajimi-desktop-pet';

// ---------------------------------------------------------------- 输出

const useColor = process.stdout.isTTY && !process.env.NO_COLOR;
const paint = (code) => (s) => (useColor ? `\u001b[${code}m${s}\u001b[0m` : String(s));
const bold = paint(1);
const dim = paint(2);
const red = paint(31);
const green = paint(32);
const yellow = paint(33);
const cyan = paint(36);

const ok = (m) => console.log(`${green('✔')} ${m}`);
const bad = (m) => console.error(`${red('✖')} ${m}`);
const warn = (m) => console.log(`${yellow('!')} ${m}`);
const info = (m) => console.log(`${cyan('›')} ${m}`);
const detail = (m) => console.log(`  ${dim(m)}`);

// ---------------------------------------------------------------- 路径

function installRoot() {
  if (process.env.HAJIMI_HOME) return path.resolve(process.env.HAJIMI_HOME);
  const local = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
  return path.join(local, 'HajimiPet');
}

const stateFile = () => path.join(installRoot(), 'state.json');

function startMenuDir() {
  const appData = process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming');
  return path.join(appData, 'Microsoft', 'Windows', 'Start Menu', 'Programs');
}

const desktopDir = () => path.join(process.env.USERPROFILE || os.homedir(), 'Desktop');

function startupDir() {
  const appData = process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming');
  return path.join(appData, 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Startup');
}

const LNK_NAME = '哈基米桌宠.lnk';

// ---------------------------------------------------------------- 状态

function loadState() {
  try {
    const parsed = JSON.parse(fs.readFileSync(stateFile(), 'utf8'));
    if (parsed && typeof parsed.pets === 'object') return parsed;
  } catch {
    /* 首次安装没有状态文件 */
  }
  return { version: 1, pets: {} };
}

function saveState(state) {
  const file = stateFile();
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(state, null, 2), 'utf8');
  fs.renameSync(tmp, file);
}

// ---------------------------------------------------------------- 注册表

function findPet(id) {
  const pet = REGISTRY.pets.find((p) => p.id === id);
  if (!pet) {
    throw new Error(`找不到桌宠「${id}」。可用：${REGISTRY.pets.map((p) => p.id).join(', ')}`);
  }
  return pet;
}

function platformKey() {
  const arch = process.arch === 'arm64' ? 'arm64' : 'x64';
  return `${process.platform}-${arch}`;
}

/** 包内自带的二进制 */
function bundledBinary() {
  const candidates = [
    path.join(__dirname, '..', 'assets', 'HajimiPet.exe'),
    path.join(__dirname, '..', 'assets', 'hajimi-pet.exe'),
  ];
  for (const c of candidates) if (fs.existsSync(c)) return c;
  return null;
}

// ---------------------------------------------------------------- 快捷方式

const psQuote = (s) => `'${String(s).replace(/'/g, "''")}'`;

function createShortcut(lnk, target, description) {
  fs.mkdirSync(path.dirname(lnk), { recursive: true });
  const script = [
    '$ws = New-Object -ComObject WScript.Shell',
    `$s = $ws.CreateShortcut(${psQuote(lnk)})`,
    `$s.TargetPath = ${psQuote(target)}`,
    `$s.WorkingDirectory = ${psQuote(path.dirname(target))}`,
    `$s.Description = ${psQuote(description)}`,
    '$s.Save()',
  ].join('; ');
  spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    stdio: 'ignore',
    windowsHide: true,
  });
  return fs.existsSync(lnk);
}

function removeFile(file) {
  try {
    if (fs.existsSync(file)) fs.unlinkSync(file);
  } catch {
    /* 忽略 */
  }
}

// ---------------------------------------------------------------- 命令

function requireWindows() {
  if (process.platform !== 'win32') {
    throw new Error(`桌宠目前只支持 Windows（当前系统：${process.platform}）`);
  }
}

function install(id, opts) {
  requireWindows();
  const pet = findPet(id);
  const binary = bundledBinary();
  if (!binary) {
    throw new Error('安装包内缺少 HajimiPet.exe，请重新安装 hajimi-pet-cli');
  }

  const targetDir = path.join(installRoot(), pet.id, PKG.version);
  fs.mkdirSync(targetDir, { recursive: true });
  const target = path.join(targetDir, 'HajimiPet.exe');
  fs.copyFileSync(binary, target);

  const state = loadState();
  const shortcuts = [];

  if (!opts.noShortcut) {
    const startLnk = path.join(startMenuDir(), LNK_NAME);
    if (createShortcut(startLnk, target, '哈基米桌宠')) shortcuts.push(startLnk);

    if (opts.desktop) {
      const deskLnk = path.join(desktopDir(), LNK_NAME);
      if (createShortcut(deskLnk, target, '哈基米桌宠')) shortcuts.push(deskLnk);
    }
    if (opts.autostart) {
      const autoLnk = path.join(startupDir(), LNK_NAME);
      if (createShortcut(autoLnk, target, '哈基米桌宠')) shortcuts.push(autoLnk);
    }
  }

  state.pets[pet.id] = {
    id: pet.id,
    name: pet.name,
    version: PKG.version,
    path: target,
    installedAt: new Date().toISOString(),
    shortcuts,
  };
  saveState(state);

  // 只保留当前版本的目录
  const petDir = path.join(installRoot(), pet.id);
  for (const entry of fs.readdirSync(petDir)) {
    if (entry !== PKG.version) fs.rmSync(path.join(petDir, entry), { recursive: true, force: true });
  }

  ok(`${pet.name} ${PKG.version} 安装完成`);
  detail(`安装位置：${targetDir}`);
  detail(`体积：${Math.round(fs.statSync(target).size / 1024)} KB（原生 exe，无 Electron、无 Node 依赖）`);
  for (const lnk of shortcuts) detail(`快捷方式：${lnk}`);
  info('从开始菜单搜索「哈基米桌宠」即可启动。');
  return 0;
}

function list(opts) {
  const pets = Object.values(loadState().pets);
  if (pets.length === 0) {
    info('还没有安装桌宠。');
    detail(`安装根目录：${installRoot()}`);
    info('安装命令：npx hajimi-pet-cli install hajimi-desktop-pet');
    return 0;
  }

  console.log(bold(`已安装桌宠（${pets.length}）`));
  for (const pet of pets) {
    const exists = fs.existsSync(pet.path);
    console.log(`\n  ${green('●')} ${bold(pet.name)} ${dim(`(${pet.id})`)}`);
    console.log(`    版本：${pet.version}`);
    console.log(`    位置：${pet.path} ${exists ? '' : red('(文件缺失)')}`);
    console.log(`    安装时间：${pet.installedAt}`);
    if (opts.verbose && pet.shortcuts.length) console.log(`    快捷方式：${pet.shortcuts.join(' | ')}`);
  }
  console.log('');
  return 0;
}

function uninstall(id) {
  const state = loadState();
  const pet = state.pets[id];
  if (!pet) {
    warn(`未安装「${id}」，无需卸载`);
    return 0;
  }

  for (const lnk of pet.shortcuts || []) removeFile(lnk);
  removeFile(path.join(startMenuDir(), LNK_NAME));
  removeFile(path.join(desktopDir(), LNK_NAME));
  removeFile(path.join(startupDir(), LNK_NAME));

  const dir = path.join(installRoot(), id);
  fs.rmSync(dir, { recursive: true, force: true });
  delete state.pets[id];
  saveState(state);

  ok(`已卸载 ${pet.name}`);
  detail(`移除目录：${dir}`);
  return 0;
}

function update(id) {
  const state = loadState();
  if (!state.pets[id]) warn(`未安装「${id}」，直接执行安装`);
  // 二进制随包分发，所以「更新」= 用当前包里的版本覆盖安装
  return install(id, { noShortcut: false, desktop: false, autostart: false });
}

const HELP = `${bold('哈基米桌宠 CLI')} ${dim(`v${PKG.version}`)}

用法：
  npx hajimi-pet-cli <命令> [桌宠ID] [选项]

命令：
  install [id]      安装桌宠（默认 ${DEFAULT_PET}）
  update  [id]      更新到本包携带的版本
  uninstall [id]    卸载并清理快捷方式
  list              查看本机已安装的桌宠
  help              显示帮助

选项：
  --desktop      同时创建桌面快捷方式
  --autostart    创建开机自启快捷方式
  --no-shortcut  不创建任何快捷方式
  --verbose      显示更多细节
  -h, --help     显示帮助
  -v, --version  显示版本

示例：
  npx hajimi-pet-cli install
  npx hajimi-pet-cli install hajimi-desktop-pet --desktop
  npx hajimi-pet-cli list
  npx hajimi-pet-cli uninstall

说明：
  · 桌宠是约 700KB 的原生 Windows exe，随 npm 包分发，安装过程不联网；
  · 默认安装到 %LOCALAPPDATA%\\HajimiPet\\<id>\\<版本>；
  · 桌宠本体不需要 Node.js，只有本安装器需要；
  · 只做复制文件与创建快捷方式，不执行任何来源不明的脚本。`;

function parse(args) {
  const opts = {
    command: '',
    id: DEFAULT_PET,
    desktop: false,
    autostart: false,
    noShortcut: false,
    verbose: false,
    help: false,
    version: false,
  };
  const positional = [];

  for (const arg of args) {
    switch (arg) {
      case '-h':
      case '--help':
        opts.help = true;
        break;
      case '-v':
      case '--version':
        opts.version = true;
        break;
      case '--desktop':
        opts.desktop = true;
        break;
      case '--autostart':
        opts.autostart = true;
        break;
      case '--no-shortcut':
        opts.noShortcut = true;
        break;
      case '--verbose':
        opts.verbose = true;
        break;
      default:
        if (arg.startsWith('-')) throw new Error(`未知选项：${arg}`);
        positional.push(arg);
    }
  }

  opts.command = positional[0] || '';
  if (positional[1]) opts.id = positional[1];
  return opts;
}

async function main(argv) {
  let opts;
  try {
    opts = parse(argv);
  } catch (error) {
    bad(error.message);
    console.log(`\n${HELP}\n`);
    return 1;
  }

  if (opts.version && !opts.command) {
    console.log(PKG.version);
    return 0;
  }
  if (opts.help || !opts.command || opts.command === 'help') {
    console.log(`\n${HELP}\n`);
    return 0;
  }

  try {
    switch (opts.command) {
      case 'install':
        return install(opts.id, opts);
      case 'update':
        return update(opts.id);
      case 'uninstall':
        return uninstall(opts.id);
      case 'list':
        return list(opts);
      default:
        bad(`未知命令：${opts.command}`);
        info('可用命令：install / update / uninstall / list / help');
        return 1;
    }
  } catch (error) {
    bad(error && error.message ? error.message : String(error));
    if (opts.verbose && error && error.stack) detail(error.stack);
    return 1;
  }
}

module.exports = { main, installRoot, loadState, platformKey, DEFAULT_PET };