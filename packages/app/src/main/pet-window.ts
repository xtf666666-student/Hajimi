import { BrowserWindow, screen } from 'electron';
import { IPC } from '../shared/ipc';
import type { PetSnapshot, Settings } from '../shared/types';
import { clampPosition, defaultPosition, petWindowSize, pointInEllipse } from './layout';import { PRELOAD_PATH, rendererUrl } from './paths';

/**
 * 桌宠窗口：无边框 + 真透明 + 置顶 + 不进任务栏。
 * 拖动在主进程按光标轮询，透明区域自动穿透。
 */
export class PetWindow {
  private win: BrowserWindow | null = null;
  private ignoreMouse = false;
  private dragging = false;
  private dragOffset = { x: 0, y: 0 };
  private dragTimer: NodeJS.Timeout | null = null;
  private hitTimer: NodeJS.Timeout | null = null;
  private cat = { x: 0, y: 0, width: 100, height: 100 };
  private listExpanded = false;

  constructor(private readonly onDrop: (position: { x: number; y: number }) => void) {}

  get browserWindow(): BrowserWindow | null {
    return this.win;
  }

  get visible(): boolean {
    return !!this.win && !this.win.isDestroyed() && this.win.isVisible();
  }

  get draggingNow(): boolean {
    return this.dragging;
  }

  create(settings: Settings, saved: { x: number; y: number } | null): BrowserWindow {
    const { width, height } = petWindowSize(settings.size);
    const target = saved ? clampPosition(saved, settings.size) : defaultPosition(settings.size);

    const win = new BrowserWindow({
      width,
      height,
      x: target.x,
      y: target.y,
      transparent: true,
      backgroundColor: '#00000000',
      frame: false,
      hasShadow: false,
      resizable: false,
      maximizable: false,
      minimizable: false,
      fullscreenable: false,
      skipTaskbar: true,
      show: false,
      alwaysOnTop: true,
      title: '哈基米 · DSH',
      webPreferences: {
        preload: PRELOAD_PATH,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
        spellcheck: false,
        backgroundThrottling: false,
      },
    });

    win.setAlwaysOnTop(true, 'floating');
    win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
    if (process.env.HAJIMI_DEVTOOLS === '1') win.webContents.openDevTools({ mode: 'detach' });

    win.once('ready-to-show', () => {
      if (settings.visible) win.showInactive();
    });
    win.on('closed', () => {
      this.stopTimers();
      this.win = null;
    });
    win.webContents.on('will-navigate', (event) => event.preventDefault());
    win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

    void win.loadURL(rendererUrl('pet'));
    this.win = win;
    this.startHitTest();
    return win;
  }

  applySettings(settings: Settings): void {
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    const bounds = win.getBounds();
    const { width, height } = petWindowSize(settings.size, this.listExpanded);
    if (bounds.width !== width || bounds.height !== height) {
      const bottom = bounds.y + bounds.height;
      const next = clampPosition(
        { x: bounds.x + (bounds.width - width) / 2, y: bottom - height },
        settings.size,
      );
      win.setBounds({ x: next.x, y: next.y, width, height });
    }
  }

  show(): void {
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    win.showInactive();
    win.setAlwaysOnTop(true, 'floating');
  }

  hide(): void {
    this.win?.hide();
    this.applyIgnore(false);
  }

  setCatRect(rect: { x: number; y: number; width: number; height: number }): void {
    if (rect.width > 0 && rect.height > 0) this.cat = rect;
  }

  /** 展开/收起对话列表：向屏幕上方长高，底部锚点不动，避免挡住任务栏。 */
  setListExpanded(expanded: boolean, size: number): void {
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    const bounds = win.getBounds();
    const next = petWindowSize(size, expanded);
    if (bounds.width === next.width && bounds.height === next.height) return;
    const bottom = bounds.y + bounds.height;
    win.setBounds({
      x: Math.round(bounds.x + (bounds.width - next.width) / 2),
      y: Math.round(bottom - next.height),
      width: next.width,
      height: next.height,
    });
    this.listExpanded = expanded;
  }

  startDrag(): void {
    const win = this.win;
    if (!win || win.isDestroyed() || this.dragging) return;
    const cursor = screen.getCursorScreenPoint();
    const bounds = win.getBounds();
    this.dragOffset = { x: cursor.x - bounds.x, y: cursor.y - bounds.y };
    this.dragging = true;
    this.applyIgnore(false);
    this.stopDragTimer();
    this.dragTimer = setInterval(() => {
      const current = screen.getCursorScreenPoint();
      win.setPosition(
        Math.round(current.x - this.dragOffset.x),
        Math.round(current.y - this.dragOffset.y),
        false,
      );
    }, 16);
    this.dragTimer.unref?.();
  }

  endDrag(): void {
    if (!this.dragging) return;
    this.dragging = false;
    this.stopDragTimer();
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    const bounds = win.getBounds();
    const size = Math.max(72, bounds.height - 30);
    const clamped = clampPosition({ x: bounds.x, y: bounds.y }, size);
    win.setPosition(clamped.x, clamped.y, false);
    this.onDrop(clamped);
    win.webContents.send(IPC.dropped);
    this.evaluateHit();
  }

  send(channel: string, payload?: unknown): void {
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    win.webContents.send(channel, payload);
  }

  broadcast(snapshot: PetSnapshot): void {
    this.send(IPC.state, snapshot);
  }

  destroy(): void {
    this.stopTimers();
    if (this.win && !this.win.isDestroyed()) this.win.destroy();
    this.win = null;
  }

  private startHitTest(): void {
    if (this.hitTimer) return;
    this.hitTimer = setInterval(() => this.evaluateHit(), 80);
    this.hitTimer.unref?.();
  }

  private evaluateHit(): void {
    const win = this.win;
    if (!win || win.isDestroyed() || !win.isVisible() || this.dragging) return;
    const cursor = screen.getCursorScreenPoint();
    const bounds = win.getBounds();
    const inside =
      cursor.x >= bounds.x &&
      cursor.x < bounds.x + bounds.width &&
      cursor.y >= bounds.y &&
      cursor.y < bounds.y + bounds.height;
    if (!inside) {
      this.applyIgnore(true);
      return;
    }
    const localX = cursor.x - bounds.x;
    const localY = cursor.y - bounds.y;
    this.applyIgnore(!pointInEllipse(localX, localY, this.cat, 0.10));
  }

  private applyIgnore(ignore: boolean): void {
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    if (ignore === this.ignoreMouse) return;
    this.ignoreMouse = ignore;
    if (ignore) win.setIgnoreMouseEvents(true, { forward: true });
    else win.setIgnoreMouseEvents(false);
  }

  private stopDragTimer(): void {
    if (this.dragTimer) {
      clearInterval(this.dragTimer);
      this.dragTimer = null;
    }
  }

  private stopTimers(): void {
    this.stopDragTimer();
    if (this.hitTimer) {
      clearInterval(this.hitTimer);
      this.hitTimer = null;
    }
  }
}
