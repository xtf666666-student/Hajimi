#!/usr/bin/env node
/**
 * 把 CLI 打成 npm tarball，并打印安装命令。
 * 用 spawn 直接调 npm，避免 execFileSync 在 Windows 上找不到 npm.cmd。
 */
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, rmSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const cli = join(root, 'packages/cli');
const exe = join(cli, 'assets/HajimiPet.exe');

if (!existsSync(exe)) {
  console.error('缺少 packages/cli/assets/HajimiPet.exe，请先运行 npm run build');
  process.exit(1);
}

for (const f of readdirSync(cli)) {
  if (f.startsWith('hajimi-pet-cli-') && f.endsWith('.tgz')) rmSync(join(cli, f));
}

const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';
const res = spawnSync(npm, ['pack'], { cwd: cli, stdio: 'inherit', shell: true });
if (res.status !== 0) {
  console.error('npm pack 失败');
  process.exit(res.status ?? 1);
}

const tgz = readdirSync(cli).find((f) => f.startsWith('hajimi-pet-cli-') && f.endsWith('.tgz'));
if (!tgz) {
  console.error('没有生成 tarball');
  process.exit(1);
}

const size = statSync(join(cli, tgz)).size;
console.log(`\n打包完成：${tgz}（${(size / 1024).toFixed(1)} KB）`);
console.log('\n本地安装测试：');
console.log(`  npx --yes ./packages/cli/${tgz} install`);
console.log('\n发布到 npm 之后：');
console.log('  npx hajimi-pet-cli install hajimi-desktop-pet');
