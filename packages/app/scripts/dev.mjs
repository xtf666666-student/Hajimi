/**
 * 开发启动器：先编译主进程 + 打包 preload，再起 Vite dev server，
 * 最后把 HAJIMI_DEV_SERVER_URL 传给 Electron，渲染层即可热更新。
 */
import { spawn, execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

const require = createRequire(import.meta.url);
const here = dirname(fileURLToPath(import.meta.url));
const appRoot = join(here, '..');

function buildMain(): void {
  const tsc = require.resolve('typescript/bin/tsc');
  execFileSync(process.execPath, [tsc, '-p', 'tsconfig.main.json'], {
    cwd: appRoot,
    stdio: 'inherit',
  });
  const esbuild = require.resolve('esbuild/bin/esbuild');
  execFileSync(
    process.execPath,
    [
      esbuild,
      'src/preload/index.ts',
      '--bundle',
      '--platform=node',
      '--format=cjs',
      '--target=node20',
      '--external:electron',
      '--outfile=dist/main/preload/index.js',
    ],
    { cwd: appRoot, stdio: 'inherit' },
  );
}

buildMain();

const server = await createServer({ configFile: join(appRoot, 'vite.config.ts') });
await server.listen();
const url = server.resolvedUrls?.local?.[0] ?? 'http://127.0.0.1:5273/';
console.log(`[dev] 渲染层：${url}`);

// 某些环境会设置 ELECTRON_RUN_AS_NODE，会让 Electron 退化成纯 Node，必须剔除
function cleanEnv(extra) {
  const env = { ...process.env, ...extra };
  delete env.ELECTRON_RUN_AS_NODE;
  return env;
}

const electronBinary = require('electron');
const child = spawn(electronBinary, ['.'], {
  cwd: appRoot,
  stdio: 'inherit',
  env: cleanEnv({ HAJIMI_DEV_SERVER_URL: url }),
});

child.on('exit', async (code) => {
  await server.close();
  process.exit(code ?? 0);
});

process.on('SIGINT', () => {
  child.kill();
});
