#!/usr/bin/env node
/**
 * 发布物端到端校验：用本地 HTTP 服务器模拟 GitHub Releases，
 * 跑一遍真实的「下载 → SHA-256 校验 → 落地 → list → uninstall」。
 *
 * 用法：node scripts/verify-release.mjs
 */
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { createServer } from 'node:http';
import { createReadStream, existsSync, mkdtempSync, readFileSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const releaseDir = join(root, 'packages/app/release');
const cliBin = join(root, 'packages/cli/bin/hajimi.js');
const manifestPath = join(root, 'pets.json');

const sha256 = (file) => createHash('sha256').update(readFileSync(file)).digest('hex');
const run = (args, env) =>
  new Promise((resolveRun) => {
    execFile(process.execPath, args, { env: { ...process.env, ...env }, timeout: 600000 }, (error, stdout, stderr) =>
      resolveRun({ code: error ? (error.code ?? 1) : 0, stdout, stderr }),
    );
  });

if (!existsSync(manifestPath)) {
  console.error('缺少 pets.json，请先运行 node scripts/make-manifest.mjs');
  process.exit(1);
}

const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const pet = manifest.pets[0];
const release = pet.releases[pet.latest];
const asset = release.assets['win32-x64']?.portable;
if (!asset) {
  console.error('pets.json 里没有 win32-x64 便携版资产');
  process.exit(1);
}

const fileName = decodeURIComponent(asset.url.split('/').pop());
const artifact = join(releaseDir, fileName);
if (!existsSync(artifact)) {
  console.error(`发布目录缺少 ${fileName}，请先 npm run dist:win`);
  process.exit(1);
}
const actualSha = sha256(artifact);

const home = mkdtempSync(join(tmpdir(), 'hajimi-release-'));
let httpServer = null;
const port = await new Promise((resolvePort) => {
  const server = createServer(async (req, res) => {
    if (req.url === '/pets.json') {
      const local = JSON.parse(JSON.stringify(manifest));
      local.pets[0].releases[pet.latest].assets['win32-x64'].portable.url =
        `http://127.0.0.1:${port}/${encodeURIComponent(fileName)}`;
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify(local));
      return;
    }
    if (req.url === `/${encodeURIComponent(fileName)}`) {
      res.writeHead(200, {
        'content-type': 'application/octet-stream',
        'content-length': String(statSync(artifact).size),
      });
      createReadStream(artifact).pipe(res);
      return;
    }
    res.writeHead(404);
    res.end('not found');
  });
  httpServer = server;
  server.listen(0, '127.0.0.1', () => resolvePort(server.address().port));
});
const shutdown = () => new Promise((done) => (httpServer ? httpServer.close(done) : done()));

const env = {
  HAJIMI_HOME: home,
  HAJIMI_REGISTRY_URL: `http://127.0.0.1:${port}/pets.json`,
  NO_COLOR: '1',
};

console.log(`发布包：${fileName} (${(statSync(artifact).size / 1024 / 1024).toFixed(1)} MB)`);
console.log(`SHA-256：${actualSha}`);
console.log(`清单记录：${asset.sha256}`);
if (actualSha !== asset.sha256) {
  console.error('✖ pets.json 中的 sha256 与实际产物不一致，请重新运行 make-manifest');
  process.exit(1);
}

const install = await run([cliBin, 'install', '--no-shortcut'], env);
console.log(install.stdout.trim());
if (install.code !== 0) {
  console.error(install.stderr);
  process.exit(1);
}

const target = join(home, 'pets', pet.id, pet.latest, 'HajimiDesktopPet.exe');
if (!existsSync(target)) {
  console.error(`✖ 安装目录缺少 ${target}`);
  process.exit(1);
}
const installedSha = sha256(target);
if (installedSha !== actualSha) {
  console.error(`✖ 安装后的文件校验失败：${installedSha}`);
  process.exit(1);
}

const listed = await run([cliBin, 'list'], env);
console.log(listed.stdout.trim());

const removed = await run([cliBin, 'uninstall'], env);
console.log(removed.stdout.trim());
if (existsSync(join(home, 'pets', pet.id))) {
  console.error('✖ 卸载后目录仍然存在');
  process.exit(1);
}

rmSync(home, { recursive: true, force: true });
await shutdown();
console.log('\n✔ 发布物校验通过：下载、SHA-256、安装、list、卸载全部正常');
