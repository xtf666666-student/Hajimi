import { app, BrowserWindow } from 'electron';
import { appendFileSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { PetApp } from './app-context';
import { registerIpc } from './ipc';
import { runSelfTest } from './selftest';

const isSelfTest = process.env.HAJIMI_SELFTEST === '1';
const selfTestDataDir = join(tmpdir(), 'hajimi-selftest');

app.setName('HajimiDesktopPet');
if (process.platform === 'win32') app.setAppUserModelId('com.hajimi.desktoppet');
if (isSelfTest) {
  // 自检用独立 userData，且每次清空，保证结果可复现
  rmSync(selfTestDataDir, { recursive: true, force: true });
  app.setPath('userData', selfTestDataDir);
}

function logCrash(kind: string, error: unknown): void {
  const message = error instanceof Error ? `${error.stack ?? error.message}` : String(error);
  console.error(`[hajimi] ${kind}:`, message);
  try {
    mkdirSync(app.getPath('userData'), { recursive: true });
    appendFileSync(
      join(app.getPath('userData'), 'crash.log'),
      `${new Date().toISOString()} ${kind}\n${message}\n\n`,
      'utf8',
    );
  } catch {
    // 日志写入失败不影响桌宠本身
  }
}

process.on('uncaughtException', (error) => logCrash('uncaughtException', error));
process.on('unhandledRejection', (reason) => logCrash('unhandledRejection', reason));

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  const petApp = new PetApp();

  app.on('second-instance', () => {
    petApp.showPet();
    petApp.openSettings();
  });

  app.whenReady().then(async () => {
    try {
      registerIpc(petApp);
      petApp.start();
    } catch (error) {
      logCrash('startup', error);
    }
    if (isSelfTest) {
      void runSelfTest(petApp);
    }
  });

  // 桌宠常驻：关闭全部窗口不代表退出，只有托盘/菜单里的“退出桌宠”才结束进程
  app.on('window-all-closed', () => {
    if (isSelfTest) app.quit();
  });

  app.on('activate', () => {
    petApp.showPet();
  });

  app.on('before-quit', () => {
    petApp.dispose();
  });

  app.on('web-contents-created', (_event, contents) => {
    contents.setWindowOpenHandler(() => ({ action: 'deny' }));
  });
}

// 兜底：避免未捕获异常导致桌宠静默消失
process.on('uncaughtException', (error) => {
  console.error('[hajimi] 未捕获异常：', error);
});

declare global {
  // eslint-disable-next-line no-var
  var __hajimiMainReady: boolean | undefined;
}
globalThis.__hajimiMainReady = true;

void BrowserWindow;
