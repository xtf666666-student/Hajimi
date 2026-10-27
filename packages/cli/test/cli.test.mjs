/**
 * hajimi-pet-cli 端到端测试。
 *
 * 用 HAJIMI_HOME 指向临时目录，真实跑 install / list / update / uninstall，
 * 并检查文件落地与清理行为。
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const bin = join(root, 'bin', 'hajimi.js');
const bundledExe = join(root, 'assets', 'HajimiPet.exe');

let home;

function runCli(args) {
  return new Promise((resolve) => {
    execFile(
      process.execPath,
      [bin, ...args],
      {
        env: { ...process.env, HAJIMI_HOME: home, NO_COLOR: '1' },
        timeout: 60000,
      },
      (error, stdout, stderr) => {
        resolve({ code: error ? (error.code ?? 1) : 0, stdout, stderr });
      },
    );
  });
}

before(() => {
  home = mkdtempSync(join(tmpdir(), 'hajimi-cli-'));
});

after(() => {
  rmSync(home, { recursive: true, force: true });
});

test('--help 与 --version 可用', async () => {
  const help = await runCli(['--help']);
  assert.equal(help.code, 0);
  assert.match(help.stdout, /install/);
  assert.match(help.stdout, /hajimi-pet-cli/);

  const version = await runCli(['--version']);
  assert.equal(version.code, 0);
  assert.match(version.stdout.trim(), /^\d+\.\d+\.\d+$/);
});

test('未知命令返回非零并给出提示', async () => {
  const result = await runCli(['bogus']);
  assert.equal(result.code, 1);
  assert.match(result.stdout + result.stderr, /未知命令|可用命令/);
});

test('未知选项返回非零', async () => {
  const result = await runCli(['install', '--nope']);
  assert.equal(result.code, 1);
  assert.match(result.stdout + result.stderr, /未知选项/);
});

test('install 会把 exe 复制到安装目录', async () => {
  const result = await runCli(['install', '--no-shortcut']);
  assert.equal(result.code, 0, result.stdout + result.stderr);

  const target = join(home, 'hajimi-desktop-pet', '1.0.0', 'HajimiPet.exe');
  assert.ok(existsSync(target), 'exe 应该落到安装目录');

  const expected = createHash('sha256').update(readFileSync(bundledExe)).digest('hex');
  const actual = createHash('sha256').update(readFileSync(target)).digest('hex');
  assert.equal(actual, expected, '复制后的 exe 必须与包内一致');

  const state = JSON.parse(readFileSync(join(home, 'state.json'), 'utf8'));
  assert.equal(state.pets['hajimi-desktop-pet'].version, '1.0.0');
  assert.ok(state.pets['hajimi-desktop-pet'].path.endsWith('HajimiPet.exe'));
});

test('安装产物是有效的 Windows PE，且体积很小', async () => {
  const target = join(home, 'hajimi-desktop-pet', '1.0.0', 'HajimiPet.exe');
  const head = readFileSync(target).subarray(0, 2).toString('ascii');
  assert.equal(head, 'MZ', 'PE 文件应以 MZ 开头');

  const kb = statSync(target).size / 1024;
  assert.ok(kb > 100, `exe 体积应大于 100KB（实际 ${kb.toFixed(0)}KB）`);
  // 轻量化目标：单文件必须远小于 5MB
  assert.ok(kb < 5 * 1024, `exe 必须保持轻量（实际 ${kb.toFixed(0)}KB）`);
});

test('list 展示已安装桌宠', async () => {
  const result = await runCli(['list']);
  assert.equal(result.code, 0);
  assert.match(result.stdout, /哈基米桌宠/);
  assert.match(result.stdout, /1\.0\.0/);
});

test('update 用当前包版本覆盖安装', async () => {
  const result = await runCli(['update']);
  assert.equal(result.code, 0, result.stdout + result.stderr);
  assert.match(result.stdout, /安装完成/);
  assert.ok(existsSync(join(home, 'hajimi-desktop-pet', '1.0.0', 'HajimiPet.exe')));
});

test('安装目录只保留当前版本', async () => {
  const stale = join(home, 'hajimi-desktop-pet', '0.9.0');
  mkdirSync(stale, { recursive: true });
  writeFileSync(join(stale, 'HajimiPet.exe'), 'stale');

  const result = await runCli(['install', '--no-shortcut']);
  assert.equal(result.code, 0);
  assert.equal(existsSync(stale), false, '旧版本目录应被清理');
});

test('uninstall 清理目录与状态', async () => {
  const result = await runCli(['uninstall']);
  assert.equal(result.code, 0);
  assert.equal(existsSync(join(home, 'hajimi-desktop-pet')), false);

  const state = JSON.parse(readFileSync(join(home, 'state.json'), 'utf8'));
  assert.equal(state.pets['hajimi-desktop-pet'], undefined);
});

test('未安装时 uninstall 不报错', async () => {
  const result = await runCli(['uninstall']);
  assert.equal(result.code, 0);
  assert.match(result.stdout, /未安装|无需卸载/);
});

test('未知桌宠 ID 报错并列出可用项', async () => {
  const result = await runCli(['install', 'not-a-real-pet']);
  assert.equal(result.code, 1);
  assert.match(result.stdout + result.stderr, /找不到桌宠|可用/);
});

test('包内携带的 exe 与注册表 sha256 一致', async () => {
  assert.ok(existsSync(bundledExe), 'npm 包必须带上桌宠二进制');
  const registry = JSON.parse(readFileSync(join(root, 'pets.json'), 'utf8'));
  const recorded = registry.pets[0].releases['1.0.0'].assets['win32-x64'].bundled.sha256;
  const actual = createHash('sha256').update(readFileSync(bundledExe)).digest('hex');
  assert.equal(actual, recorded, '注册表里的 sha256 必须与实际二进制一致');
});
