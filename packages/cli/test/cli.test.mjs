/**
 * CLI 端到端测试：本地 HTTP 服务器模拟 GitHub Releases，
 * 完整走一遍 install / list / update / uninstall 与校验失败分支。
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { createServer } from 'node:http';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const bin = join(root, 'bin', 'hajimi.js');

const fakeExe = Buffer.concat([Buffer.from('MZ'), Buffer.alloc(2048, 0x41)]);
const fakeExeV2 = Buffer.concat([Buffer.from('MZ'), Buffer.alloc(4096, 0x42)]);
const sha = (buffer) => createHash('sha256').update(buffer).digest('hex');

let server;
let port;
let home;
let handlerRef = null;

function manifest(version, { badSha = false } = {}) {
  const file = `HajimiDesktopPet-Portable-${version}-x64.exe`;
  return {
    version: 1,
    pets: [
      {
        id: 'hajimi-desktop-pet',
        name: '哈基米桌宠',
        repo: 'xtf666666-student/Hajimi',
        latest: version,
        releases: {
          [version]: {
            version,
            assets: {
              'win32-x64': {
                portable: {
                  url: `http://127.0.0.1:${port}/${file}`,
                  sha256: badSha
                    ? sha(Buffer.from('wrong'))
                    : sha(version === '1.0.1' ? fakeExeV2 : fakeExe),
                },
              },
            },
          },
        },
      },
    ],
  };
}

function serve({ manifest: manifestData, version, buffer }, contentType = 'application/octet-stream') {
  return (req, res) => {
    if (req.url === '/pets.json') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify(manifestData));
      return;
    }
    if (req.url === `/HajimiDesktopPet-Portable-${version}-x64.exe`) {
      res.writeHead(200, {
        'content-type': contentType,
        'content-length': String(buffer.length),
      });
      res.end(buffer);
      return;
    }
    res.writeHead(404);
    res.end('not found');
  };
}

/** 启动服务器；build() 在端口确定后调用，因此 manifest 里的 URL 一定正确。 */
async function startServer(build) {
  server = createServer((req, res) => handlerRef(req, res));
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  port = server.address().port;
  handlerRef = build();
}

async function stopServer() {
  if (server) await new Promise((resolve) => server.close(resolve));
  server = null;
  handlerRef = null;
}

function runCli(args) {
  return new Promise((resolve) => {
    execFile(
      process.execPath,
      [bin, ...args],
      {
        env: {
          ...process.env,
          HAJIMI_HOME: home,
          HAJIMI_REGISTRY_URL: `http://127.0.0.1:${port ?? 1}/pets.json`,
          NO_COLOR: '1',
        },
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

after(async () => {
  await stopServer();
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

test('未知命令返回非零并提示', async () => {
  const result = await runCli(['bogus']);
  assert.equal(result.code, 1);
  assert.match(result.stdout + result.stderr, /未知命令|可用命令/);
});

test('install 会真正下载、校验并落盘', async () => {
  await startServer(() => serve({ manifest: manifest('1.0.0'), version: '1.0.0', buffer: fakeExe }));

  const result = await runCli(['install', '--no-shortcut']);
  assert.equal(result.code, 0, result.stdout + result.stderr);

  const exe = join(home, 'pets', 'hajimi-desktop-pet', '1.0.0', 'HajimiDesktopPet.exe');
  assert.ok(existsSync(exe), '便携版 exe 应该被复制到安装目录');
  assert.equal(readFileSync(exe).subarray(0, 2).toString('ascii'), 'MZ');

  const state = JSON.parse(readFileSync(join(home, 'state.json'), 'utf8'));
  assert.equal(state.pets['hajimi-desktop-pet'].currentVersion, '1.0.0');
  assert.equal(state.pets['hajimi-desktop-pet'].installs[0].sha256, sha(fakeExe));

  const listed = await runCli(['list']);
  assert.equal(listed.code, 0);
  assert.match(listed.stdout, /哈基米桌宠/);
  assert.match(listed.stdout, /1\.0\.0/);
});

test('同一版本重复安装会跳过下载', async () => {
  const result = await runCli(['install', '--no-shortcut']);
  assert.equal(result.code, 0);
  assert.match(result.stdout, /已经安装/);
});

test('update 会切换到新版本并清理旧版本', async () => {
  await stopServer();
  await startServer(() => serve({ manifest: manifest('1.0.1'), version: '1.0.1', buffer: fakeExeV2 }));

  const result = await runCli(['update', '--no-shortcut']);
  assert.equal(result.code, 0, result.stdout + result.stderr);

  const oldExe = join(home, 'pets', 'hajimi-desktop-pet', '1.0.0', 'HajimiDesktopPet.exe');
  const newExe = join(home, 'pets', 'hajimi-desktop-pet', '1.0.1', 'HajimiDesktopPet.exe');
  assert.equal(existsSync(oldExe), false, '旧版本目录应被清理');
  assert.ok(existsSync(newExe));
  assert.equal(readFileSync(newExe).length, fakeExeV2.length);

  const state = JSON.parse(readFileSync(join(home, 'state.json'), 'utf8'));
  assert.equal(state.pets['hajimi-desktop-pet'].currentVersion, '1.0.1');
});

test('SHA-256 不匹配时拒绝安装', async () => {
  await stopServer();
  await startServer(() =>
    serve({ manifest: manifest('1.0.2', { badSha: true }), version: '1.0.2', buffer: fakeExe }),
  );

  const result = await runCli(['install', '--no-shortcut', '--force']);
  assert.notEqual(result.code, 0);
  assert.match(result.stdout + result.stderr, /SHA-256|校验|下载失败/);
  assert.equal(existsSync(join(home, 'pets', 'hajimi-desktop-pet', '1.0.2')), false);
});

test('非 PE 文件（错误页）会被拒绝', async () => {
  await stopServer();
  const html = Buffer.from('<html>404 rate limited</html>');
  await startServer(() => {
    const m = manifest('1.0.3');
    m.pets[0].releases['1.0.3'].assets['win32-x64'].portable.sha256 = sha(html);
    return serve({ manifest: m, version: '1.0.3', buffer: html }, 'text/html');
  });

  const result = await runCli(['install', '--no-shortcut', '--force']);
  assert.notEqual(result.code, 0);
  assert.match(result.stdout + result.stderr, /不是有效的 Windows 可执行文件|MZ/);
});

test('uninstall 清理目录与状态', async () => {
  const result = await runCli(['uninstall']);
  assert.equal(result.code, 0);
  assert.equal(existsSync(join(home, 'pets', 'hajimi-desktop-pet')), false);
  const stateFile = join(home, 'state.json');
  assert.ok(existsSync(stateFile), '卸载后仍应保留安装记录文件');
  const state = JSON.parse(readFileSync(stateFile, 'utf8'));
  assert.equal(state.pets['hajimi-desktop-pet'], undefined);
});

test('registry 不可达时回退到内置副本', async () => {
  await stopServer();
  // 内置注册表只有 1.0.0，请求不存在的版本可证明走的是内置副本
  const result = await runCli(['install', '--no-shortcut', '--force', '--release', '9.9.9']);
  assert.notEqual(result.code, 0);
  assert.match(result.stdout + result.stderr, /9\.9\.9|没有 .* 版本|不支持当前平台/);
});
