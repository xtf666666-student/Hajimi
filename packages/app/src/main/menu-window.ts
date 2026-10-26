import { BrowserWindow, screen } from 'electron';
import { IPC } from '../shared/ipc';
import type { PetSnapshot } from '../shared/types';
import { PRELOAD_PATH, rendererUrl } from './paths';

export const MENU_WIDTH = 196;
export const MENU_HEIGHT = 232;

/** 极简右键菜单：只留必要动作。 */
export class MenuWindow {
  private win: BrowserWindow | null = null;

  get isOpen(): boolean {
    return !!this.win && !this.win.isDestroyed() && this.win.isVisible();
  }

  open(at: { x: number; y: number }, snapshot: PetSnapshot): void {
    this.close();
    const display = screen.getDisplayNearestPoint({ x: Math.round(at.x), y: Math.round(at.y) });
    const { workArea } = display;
    const x = Math.round(
      Math.min(Math.max(at.x, workArea.x + 6), workArea.x + workArea.width - MENU_WIDTH - 6),
    );
    const y = Math.round(
      Math.min(Math.max(at.y, workArea.y + 6), workArea.y + workArea.height - MENU_HEIGHT - 6),
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
    win.once('ready-to-show', () => win.show());
    win.on('blur', () => this.close());
    win.on('closed', () => {
      this.win = null;
    });
    void win.loadURL(rendererUrl('menu'));
    this.win = win;
    win.webContents.once('did-finish-load', () => this.send(IPC.state, snapshot));
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
