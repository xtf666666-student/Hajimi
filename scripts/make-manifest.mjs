#!/usr/bin/env node
/**
 * 根据 packages/app/release 下真实产出的安装包生成 pets.json（注册表），
 * 同时写入仓库根目录（供 raw.githubusercontent 直接读取）与 CLI 包内副本。
 *
 * 用法：node scripts/make-manifest.mjs [--version 1.0.0] [--dir packages/app/release]
 */
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);

function arg(name, fallback) {
  const index = args.indexOf(`--${name}`);
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback;
}

const appPkg = JSON.parse(readFileSync(join(root, 'packages/app/package.json'), 'utf8'));
const version = arg('version', appPkg.version);
const releaseDir = resolve(root, arg('dir', 'packages/app/release'));
const baseUrl = arg(
  'base',
  `https://github.com/xtf666666-student/Hajimi/releases/download/v${version}`,
);

if (!existsSync(releaseDir)) {
  console.error(`找不到发布目录：${releaseDir}`);
  process.exit(1);
}

const files = readdirSync(releaseDir);
const portable = files.find((f) => /Portable.*\.exe$/i.test(f));
const installer = files.find((f) => /Setup.*\.exe$/i.test(f));

if (!portable && !installer) {
  console.error(`发布目录里没有找到 .exe 安装包：${releaseDir}`);
  process.exit(1);
}

const sha256 = (file) => createHash('sha256').update(readFileSync(join(releaseDir, file))).digest('hex');

const assets = {};
const win = {};
if (portable) win.portable = { url: `${baseUrl}/${encodeURIComponent(portable)}`, sha256: sha256(portable) };
if (installer) win.installer = { url: `${baseUrl}/${encodeURIComponent(installer)}`, sha256: sha256(installer) };
if (Object.keys(win).length > 0) assets['win32-x64'] = win;

const manifest = {
  version: 1,
  updatedAt: new Date().toISOString(),
  pets: [
    {
      id: 'hajimi-desktop-pet',
      name: '哈基米桌宠',
      description: '常驻 Windows 桌面的哈基米猫猫桌宠：可拖动、会眨眼、会吐槽，支持 3D 视差与摸头喂食。',
      repo: 'xtf666666-student/Hajimi',
      homepage: 'https://github.com/xtf666666-student/Hajimi',
      latest: version,
      releases: {
        [version]: {
          version,
          releasedAt: new Date().toISOString().slice(0, 10),
          assets,
        },
      },
    },
  ],
};

const text = `${JSON.stringify(manifest, null, 2)}\n`;
for (const target of ['pets.json', 'packages/cli/pets.json', 'packages/app/release/pets.json']) {
  writeFileSync(join(root, target), text, 'utf8');
  console.log(`写入 ${target}`);
}

// SHA256SUMS.txt：便于人工核对与镜像站使用
const sums = [];
for (const file of [portable, installer].filter(Boolean)) {
  sums.push(`${sha256(file)}  ${file}`);
}
writeFileSync(join(releaseDir, 'SHA256SUMS.txt'), `${sums.join('\n')}\n`, 'utf8');
console.log(`已生成 SHA256SUMS.txt（${sums.length} 个文件）`);
