import { app } from 'electron';
import { appendFileSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { registerIpc } from './ipc';
import { PetApp } from './pet-app';
import { runSelfTest } from './selftest';

const isSelfTest = process.env.HAJIMI_SELFTEST === '1';
const selfTestDir = join(tmpdir(), 'hajimi-dsh-selftest');

app.setName('HajimiDshPet');
if (process.platform === 'win32') app.setAppUserModelId('com.hajimi.dshpet');
if (isSelfTest) {
  rmSync(selfTestDir, { recursive: true, force: true });
  app.setPath('userData', selfTestDir);
}

function logCrash(kind: string, error: unknown): void {
  const message = error instanceof Error ? (error.stack ?? error.message) : String(error);
  console.error(`[hajimi] ${kind}:`, message);
  try {
    mkdirSync(app.getPath('userData'), { recursive: true });
    appendFileSync(
      join(app.getPath('userData'), 'crash.log'),
      `${new Date().toISOString()} ${kind}\n${message}\n\n`,
      'utf8',
    );
  } catch {
    // 记录日志失败不影响桌宠
  }
}

process.on('uncaughtException', (error) => logCrash('uncaughtException', error));
process.on('unhandledRejection', (reason) => logCrash('unhandledRejection', reason));

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  const petApp = new PetApp();

  app.on('second-instance', () => {
    petApp.updateSettings({ visible: true });
  });

  app.whenReady().then(() => {
    try {
      registerIpc(petApp);
      petApp.start();
    } catch (error) {
      logCrash('startup', error);
    }
    if (isSelfTest) void runSelfTest(petApp);
  });

  // 桌宠常驻：窗口全关也不退出，只有菜单里的「退出」才结束
  app.on('window-all-closed', () => {
    if (isSelfTest) app.quit();
  });

  app.on('before-quit', () => petApp.dispose());

  app.on('web-contents-created', (_event, contents) => {
    contents.setWindowOpenHandler(() => ({ action: 'deny' }));
  });
}

export { writeFileSync };
