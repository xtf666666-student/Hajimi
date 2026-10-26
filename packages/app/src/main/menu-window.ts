import { BrowserWindow, screen } from 'electron';
import { IPC } from '../shared/ipc';
import type { Point } from '../shared/types';
import { PRELOAD_PATH, rendererUrl } from './paths';

export const MENU_WIDTH = 214;
export const MENU_HEIGHT = 286;

/** 自定义右键菜单：独立透明窗口，样式与桌宠统一，不弹原生菜单。 */
export class MenuWindow {
  private win: BrowserWindow | null = null;

  get isOpen(): boolean {
    return !!this.win && !this.win.isDestroyed() && this.win.isVisible();
  }

  open(at: Point): void {
    this.close();
    const display = screen.getDisplayNearestPoint({ x: Math.round(at.x), y: Math.round(at.y) });
    const { workArea } = display;
    const x = Math.round(
      Math.min(Math.max(at.x, workArea.x + 8), workArea.x + workArea.width - MENU_WIDTH - 8),
    );
    const y = Math.round(
      Math.min(Math.max(at.y, workArea.y + 8), workArea.y + workArea.height - MENU_HEIGHT - 8),
    );

    const win = new BrowserWindow({
      width: MENU_WIDTH,
      height: MENU_HEIGHT,
      x,
      y,
      transparent: true,
      backgroundColor: '#00000000',
      frame: false,
      hasShadow: false,
      resizable: false,
      movable: false,
      minimizable: false,
      maximizable: false,
      fullscreenable: false,
      skipTaskbar: true,
      show: false,
      alwaysOnTop: true,
      title: '哈基米菜单',
      webPreferences: {
        preload: PRELOAD_PATH,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
        spellcheck: false,
      },
    });
    win.setAlwaysOnTop(true, 'pop-up-menu');
    win.webContents.on('will-navigate', (event) => event.preventDefault());
    win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    win.once('ready-to-show', () => {
      win.show();
      win.focus();
    });
    win.on('blur', () => this.close());
    win.on('closed', () => {
      this.win = null;
    });
    void win.loadURL(rendererUrl('menu'));
    this.win = win;
  }

  close(): void {
    const win = this.win;
    this.win = null;
    if (win && !win.isDestroyed()) win.destroy();
  }

  send(channel: string, payload?: unknown): void {
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    win.webContents.send(channel, payload);
  }

  destroy(): void {
    this.close();
  }
}
