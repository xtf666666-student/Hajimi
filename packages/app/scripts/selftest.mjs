/**
 * 自检启动器：构建后以 HAJIMI_SELFTEST=1 启动 Electron，
 * 主进程会真实创建窗口、截图并写出 report.json。
 */
import { spawn, execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const here = dirname(fileURLToPath(import.meta.url));
const appRoot = join(here, '..');

execFileSync(process.execPath, [require.resolve('typescript/bin/tsc'), '-p', 'tsconfig.main.json'], {
  cwd: appRoot,
  stdio: 'inherit',
});
execFileSync(
  process.execPath,
  [
    require.resolve('esbuild/bin/esbuild'),
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
  env: cleanEnv({ HAJIMI_SELFTEST: '1' }),
});

child.on('exit', (code) => {
  console.log(`[selftest] Electron 退出码：${code ?? 0}`);
  process.exit(code ?? 0);
});
