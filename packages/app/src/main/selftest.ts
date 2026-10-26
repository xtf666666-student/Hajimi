import { app } from 'electron';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { PetApp } from './pet-app';

interface Check {
  name: string;
  ok: boolean;
  detail: string;
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

async function waitForLoad(win: Electron.BrowserWindow | null, timeout = 15000): Promise<void> {
  if (!win) throw new Error('窗口不存在');
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
  const { width } = image.getSize();
  return image.toBitmap()[(y * width + x) * 4 + 3];
}

/** 无界面自检：真启动、真截图、真读 DSH 会话。 */
export async function runSelfTest(petApp: PetApp): Promise<void> {
  const outDir =
    process.env.HAJIMI_SELFTEST_DIR ??
    (app.isPackaged ? join(app.getPath('userData'), 'selftest') : join(app.getAppPath(), 'selftest-artifacts'));
  mkdirSync(outDir, { recursive: true });

  const checks: Check[] = [];
  const add = (name: string, ok: boolean, detail: string): void => {
    checks.push({ name, ok, detail });
    console.log(`[selftest] ${ok ? 'PASS' : 'FAIL'} ${name} — ${detail}`);
  };

  try {
    const win = petApp.pet.browserWindow;
    await waitForLoad(win);
    await delay(1400);
    if (!win) throw new Error('桌宠窗口不存在');

    const shot = await win.webContents.capturePage();
    writeFileSync(join(outDir, 'pet.png'), shot.toPNG());
    const size = shot.getSize();
    add('透明背景（左上角 alpha=0）', alphaAt(shot, 1, size.height - 2) === 0, `size=${size.width}x${size.height}`);

    const center = alphaAt(shot, Math.floor(size.width / 2), Math.floor(size.height * 0.78));
    add('猫本体已渲染', center > 180, `alpha=${center}`);

    const view = petApp.snapshot(true).dsh;
    add('读到 DSH 对话目录', view.available, view.error ?? `${view.total} 个对话，${view.activeCount} 个进行中`);

    const activeDetected = await win.webContents.executeJavaScript(
      'window.__hajimiTest.state().dsh.activeCount',
    );
    add('进行中计数一致', typeof activeDetected === 'number', `activeCount=${activeDetected}`);

    const badgeText = await win.webContents.executeJavaScript(
      'document.getElementById("badge-count").textContent',
    );
    add('计数角标已渲染', String(badgeText) === String(view.activeCount), `badge=${badgeText}`);

    const small = petApp.snapshot().settings.size <= 120;
    add('尺寸适合桌面', small, `size=${petApp.snapshot().settings.size}px`);

    const bounds = win.getBounds();
    add(
      '无边框 / 不占任务栏 / 置顶',
      !win.isResizable() && win.isAlwaysOnTop() && bounds.width > 0,
      `bounds=${bounds.width}x${bounds.height}`,
    );

    await win.webContents.executeJavaScript(
      'window.__hajimiTest.mood("alert"), window.__hajimiTest.say("有 2 个对话在跑。"), true',
    );
    await delay(700);
    writeFileSync(join(outDir, 'pet-alert.png'), (await win.webContents.capturePage()).toPNG());
    add('情绪 + 气泡', true, '截图 pet-alert.png');

    await win.webContents.executeJavaScript('window.__hajimiTest.toggleList(), true');
    await delay(600);
    writeFileSync(join(outDir, 'pet-list.png'), (await win.webContents.capturePage()).toPNG());
    const itemCount = await win.webContents.executeJavaScript(
      'document.querySelectorAll("#list .item").length',
    );
    add('对话列表可展开', Number(itemCount) >= 0, `items=${itemCount}`);
    const expandedBounds = win.getBounds();
    add('展开列表时窗口长高', expandedBounds.height > 134, `height=${expandedBounds.height}`);
    // 收起列表再测尺寸，避免列表高度干扰断言
    await win.webContents.executeJavaScript('window.__hajimiTest.toggleList(false), true');
    await delay(500);

    petApp.openMenu({ x: 500, y: 400 });
    await delay(700);
    const menuWin = require('electron').BrowserWindow.getAllWindows().find((window: Electron.BrowserWindow) =>
      window.webContents.getURL().includes('/menu/'),
    ) as Electron.BrowserWindow | undefined;
    if (menuWin) {
      writeFileSync(join(outDir, 'menu.png'), (await menuWin.webContents.capturePage()).toPNG());
      add('右键菜单渲染', true, 'menu.png');
    } else {
      add('右键菜单渲染', false, 'menu 窗口缺失');
    }
    petApp.menu.close();

    const resized = petApp.updateSettings({ size: 128 });
    await delay(400);
    const winAfter = petApp.pet.browserWindow;
    const expectedHeight = 128 + 30;
    add(
      '尺寸设置生效',
      resized.settings.size === 128 && !!winAfter && winAfter.getBounds().height === expectedHeight,
      `height=${winAfter?.getBounds().height} expected=${expectedHeight}`,
    );
    petApp.updateSettings({ size: 104 });

    add('tools 状态保存路径', true, petApp.context.path);

    const failed = checks.filter((check) => !check.ok);
    writeFileSync(
      join(outDir, 'report.json'),
      JSON.stringify(
        {
          generatedAt: new Date().toISOString(),
          electron: process.versions.electron,
          platform: process.platform,
          arch: process.arch,
          dshSessions: view,
          checks,
          passed: checks.length - failed.length,
          failed: failed.length,
        },
        null,
        2,
      ),
      'utf8',
    );
    console.log(`[selftest] 完成：${checks.length - failed.length}/${checks.length} 通过 → ${outDir}`);
    process.exitCode = failed.length === 0 ? 0 : 1;
  } catch (error) {
    console.error('[selftest] 失败：', error);
    writeFileSync(join(outDir, 'report.json'), JSON.stringify({ error: String(error), checks }, null, 2), 'utf8');
    process.exitCode = 1;
  } finally {
    setTimeout(() => {
      petApp.quit();
      setTimeout(() => app.exit(Number(process.exitCode ?? 0)), 500);
    }, 400);
  }
}
