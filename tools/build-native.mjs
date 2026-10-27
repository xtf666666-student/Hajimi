#!/usr/bin/env node
/**
 * 构建原生桌宠：dotnet publish -> packages/native/bin -> 拷进 CLI 包
 * 然后刷新 pets.json 里的 sha256。
 */
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const native = join(root, 'packages/native');
const built = join(native, 'bin/Release/net48/HajimiPet.exe');
const cliAssets = join(root, 'packages/cli/assets/HajimiPet.exe');
const registryFile = join(root, 'packages/cli/pets.json');

console.log('构建原生桌宠…');
execFileSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], {
  cwd: native,
  stdio: 'inherit',
});

if (!existsSync(built)) {
  console.error('构建产物不存在：' + built);
  process.exit(1);
}

copyFileSync(built, cliAssets);
const size = statSync(cliAssets).size;
console.log(`已复制到 CLI 包：${(size / 1024).toFixed(1)} KB`);

// 刷新注册表里的 sha256
const sha = createHash('sha256').update(readFileSync(cliAssets)).digest('hex');
const registry = JSON.parse(readFileSync(registryFile, 'utf8'));
registry.updatedAt = new Date().toISOString();
registry.pets[0].releases[registry.pets[0].latest].assets['win32-x64'].bundled.sha256 = sha;
writeFileSync(registryFile, `${JSON.stringify(registry, null, 2)}\n`, 'utf8');
console.log('sha256 =', sha);

const kb = size / 1024;
if (kb > 5 * 1024) {
  console.warn(`警告：二进制 ${kb.toFixed(0)}KB 超出轻量化目标（5MB）`);
} else {
  console.log(`轻量化检查通过：${kb.toFixed(0)} KB`);
}
