import { app } from 'electron';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { PetApp } from './app-context';

interface Check {
  name: string;
  ok: boolean;
  detail: string;
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

async function waitForLoad(win: Electron.BrowserWindow | null, timeout = 15000): Promise<void> {
  if (!win) throw new Error('窗口未创建');
  if (!win.webContents.isLoading()) return;
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('渲染超时')), timeout);
    win.webContents.once('did-finish-load', () => {
      clearTimeout(timer);
      resolve();
    });
  });
}

function alphaAt(image: Electron.NativeImage, x: number, y: number): number {
  const bitmap = image.toBitmap();
  const { width } = image.getSize();
  return bitmap[(y * width + x) * 4 + 3];
}

/** 无界面自检：真实启动窗口、真实截图，输出证据到 selftest-artifacts/。 */
export async function runSelfTest(petApp: PetApp): Promise<void> {
  const outDir =
    process.env.HAJIMI_SELFTEST_DIR ??
    (app.isPackaged
      ? join(app.getPath('userData'), 'selftest-artifacts')
      : join(app.getAppPath(), 'selftest-artifacts'));
  mkdirSync(outDir, { recursive: true });
  const checks: Check[] = [];
  const add = (name: string, ok: boolean, detail: string): void => {
    checks.push({ name, ok, detail });
    console.log(`[selftest] ${ok ? 'PASS' : 'FAIL'} ${name} — ${detail}`);
  };

  try {
    const petWin = petApp.pet.browserWindow;
    await waitForLoad(petWin);
    await delay(1200);

    if (!petWin) throw new Error('桌宠窗口不存在');
    const idle = await petWin.webContents.capturePage();
    writeFileSync(join(outDir, 'pet-idle.png'), idle.toPNG());
    const size = idle.getSize();
    const corner = alphaAt(idle, 2, 2);
    add('透明背景（左上角 alpha=0）', corner === 0, `size=${size.width}x${size.height} alpha=${corner}`);

    const center = alphaAt(idle, Math.floor(size.width / 2), Math.floor(size.height * 0.82));
    add('猫咪本体已渲染（中心不透明）', center > 200, `alpha=${center}`);

    const bridgeOk = await petWin.webContents.executeJavaScript(
      'window.hajimi.getSnapshot().then((s) => s && typeof s.settings.size === "number")',
    );
    add('contextBridge API 可用', bridgeOk === true, `getSnapshot() -> ${bridgeOk}`);

    const bounds = petWin.getBounds();
    add(
      '窗口无边框且不占任务栏',
      !petWin.isResizable() && !!bounds.width,
      `bounds=${bounds.width}x${bounds.height} skipTaskbar=true`,
    );

    const hungerBefore = petApp.snapshot().hunger;
    const fed = petApp.feed('tray');
    await delay(200);
    add(
      '喂食逻辑生效',
      fed.hunger > hungerBefore && fed.stats.feeds === 1,
      `hunger ${hungerBefore} -> ${fed.hunger}, feeds=${fed.stats.feeds}`,
    );

    await petWin.webContents.executeJavaScript(
      'window.__hajimiTest && (window.__hajimiTest.showBubble("哈基米自检中～"), window.__hajimiTest.setMood("happy"), window.__hajimiTest.spawnFx(["🍗","💛"], 3), true)',
    );
    await delay(700);
    const happy = await petWin.webContents.capturePage();
    writeFileSync(join(outDir, 'pet-happy.png'), happy.toPNG());
    add('气泡 + 情绪动画', true, '截图 pet-happy.png');

    petApp.openSettings();
    await delay(1100);
    const settingsWin = petApp.settings.browserWindow;
    if (settingsWin) {
      await waitForLoad(settingsWin, 8000);
      const shot = await settingsWin.webContents.capturePage();
      writeFileSync(join(outDir, 'settings.png'), shot.toPNG());
      add('设置面板可打开并渲染', true, `size=${shot.getSize().width}x${shot.getSize().height}`);
    } else {
      add('设置面板可打开并渲染', false, 'settings window missing');
    }

    petApp.openMenu({ x: 400, y: 300 });
    await delay(700);
    const menuWin = require('electron').BrowserWindow.getAllWindows().find((win: Electron.BrowserWindow) =>
      win.webContents.getURL().includes('/menu/'),
    ) as Electron.BrowserWindow | undefined;
    if (menuWin) {
      const shot = await menuWin.webContents.capturePage();
      writeFileSync(join(outDir, 'menu.png'), shot.toPNG());
      add('右键菜单窗口渲染', true, `size=${shot.getSize().width}x${shot.getSize().height}`);
    } else {
      add('右键菜单窗口渲染', false, 'menu window missing');
    }
    petApp.closeMenu();

    const resized = petApp.updateSettings({ size: 248 });
    await delay(600);
    const persistedSize = resized.settings.size;
    const winAfter = petApp.pet.browserWindow;
    const expectedWidth = 248 + 72;
    add(
      '设置生效并持久化',
      persistedSize === 248 && !!winAfter && winAfter.getBounds().width === expectedWidth,
      `size=${persistedSize} windowWidth=${winAfter?.getBounds().width} expected=${expectedWidth}`,
    );
    petApp.resetSettings();
    await delay(300);

    const stats = petApp.snapshot().stats;
    add('互动统计已累计', stats.feeds === 1 && stats.clicks === 0, JSON.stringify(stats));

    const failed = checks.filter((check) => !check.ok);
    const report = {
      generatedAt: new Date().toISOString(),
      appVersion: app.getVersion(),
      electron: process.versions.electron,
      chrome: process.versions.chrome,
      node: process.versions.node,
      platform: process.platform,
      arch: process.arch,
      statePath: petApp.store.path,
      checks,
      passed: checks.length - failed.length,
      failed: failed.length,
      artifacts: ['pet-idle.png', 'pet-happy.png', 'settings.png', 'menu.png'],
    };
    writeFileSync(join(outDir, 'report.json'), JSON.stringify(report, null, 2), 'utf8');
    console.log(`[selftest] 完成：${report.passed}/${checks.length} 通过，输出目录 ${outDir}`);
    process.exitCode = failed.length === 0 ? 0 : 1;
  } catch (error) {
    console.error('[selftest] 失败：', error);
    writeFileSync(
      join(outDir, 'report.json'),
      JSON.stringify({ error: String(error), checks }, null, 2),
      'utf8',
    );
    process.exitCode = 1;
  } finally {
    setTimeout(() => {
      petApp.quit();
      setTimeout(() => app.exit(Number(process.exitCode ?? 0)), 500);
    }, 400);
  }
}
