import { BrowserWindow, screen } from 'electron';
import { IPC } from '../shared/ipc';
import type { PetSnapshot } from '../shared/types';
import { PRELOAD_PATH, rendererUrl } from './paths';

export const SETTINGS_WIDTH = 452;
export const SETTINGS_HEIGHT = 788;

/** 设置面板：独立无边框窗口，关闭时只隐藏，不影响桌宠。 */
export class SettingsWindow {
  private win: BrowserWindow | null = null;

  get browserWindow(): BrowserWindow | null {
    return this.win;
  }

  open(snapshot: PetSnapshot): void {
    if (this.win && !this.win.isDestroyed()) {
      this.win.show();
      this.win.focus();
      this.win.webContents.send(IPC.stateChanged, snapshot);
      return;
    }
    const { workArea } = screen.getPrimaryDisplay();
    const win = new BrowserWindow({
      width: SETTINGS_WIDTH,
      height: SETTINGS_HEIGHT,
      x: Math.round(workArea.x + (workArea.width - SETTINGS_WIDTH) / 2),
      y: Math.round(workArea.y + (workArea.height - SETTINGS_HEIGHT) / 2),
      transparent: true,
      backgroundColor: '#00000000',
      frame: false,
      hasShadow: false,
      resizable: false,
      maximizable: false,
      minimizable: false,
      fullscreenable: false,
      skipTaskbar: false,
      show: false,
      title: '哈基米设置',
      icon: undefined,
      webPreferences: {
        preload: PRELOAD_PATH,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
        spellcheck: false,
      },
    });
    win.setMenuBarVisibility(false);
    win.webContents.on('will-navigate', (event) => event.preventDefault());
    win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    win.once('ready-to-show', () => {
      win.show();
      win.focus();
    });
    win.on('closed', () => {
      this.win = null;
    });
    void win.loadURL(rendererUrl('settings'));
    this.win = win;
    win.webContents.once('did-finish-load', () => {
      win.webContents.send(IPC.stateChanged, snapshot);
    });
  }

  close(): void {
    this.win?.hide();
  }

  send(channel: string, payload?: unknown): void {
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    win.webContents.send(channel, payload);
  }

  destroy(): void {
    if (this.win && !this.win.isDestroyed()) this.win.destroy();
    this.win = null;
  }
}
