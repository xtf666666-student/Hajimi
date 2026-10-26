import { BrowserWindow, screen } from 'electron';
import { IPC } from '../shared/ipc';
import type { PetGeometry, Point, Settings } from '../shared/types';
import { clampPosition, defaultPosition, petSizeFromWindowWidth, petWindowSize, pointInEllipse, pointInRect } from './layout';
import { PRELOAD_PATH, rendererUrl } from './paths';

/**
 * 桌宠窗口：无边框 + 真透明 + 置顶 + 不进任务栏。
 * 负责拖动（主进程按光标轮询移动，避免跟不上）、点击穿透（透明区域不挡桌面）。
 */
export class PetWindow {
  private win: BrowserWindow | null = null;
  private ignoreMouse = false;
  private dragging = false;
  private dragOffset: Point = { x: 0, y: 0 };
  private dragTimer: NodeJS.Timeout | null = null;
  private hitTimer: NodeJS.Timeout | null = null;
  private geometry: PetGeometry = {
    cat: { x: 0, y: 0, width: 200, height: 200 },
    bubble: null,
  };

  constructor(private readonly onDrop: (position: Point) => void) {}

  get browserWindow(): BrowserWindow | null {
    return this.win;
  }

  get isDragging(): boolean {
    return this.dragging;
  }

  get isVisible(): boolean {
    return !!this.win && !this.win.isDestroyed() && this.win.isVisible();
  }

  create(settings: Settings, savedPosition: Point | null): BrowserWindow {
    const { width, height } = petWindowSize(settings.size);
    const target = savedPosition ? clampPosition(savedPosition, settings.size) : defaultPosition(settings.size);

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
      alwaysOnTop: settings.alwaysOnTop,
      title: '哈基米桌宠',
      webPreferences: {
        preload: PRELOAD_PATH,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
        spellcheck: false,
        backgroundThrottling: false,
      },
    });

    win.setAlwaysOnTop(settings.alwaysOnTop, 'floating');
    win.setOpacity(settings.opacity);
    if (process.env.HAJIMI_DEVTOOLS === '1') win.webContents.openDevTools({ mode: 'detach' });

    win.once('ready-to-show', () => {
      win.showInactive();
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
    const { width, height } = petWindowSize(settings.size);
    if (bounds.width !== width || bounds.height !== height) {
      const next = clampPosition(
        {
          x: bounds.x + (bounds.width - width) / 2,
          y: bounds.y + (bounds.height - height),
        },
        settings.size,
      );
      win.setBounds({ x: next.x, y: next.y, width, height });
    }
    win.setOpacity(settings.opacity);
    win.setAlwaysOnTop(settings.alwaysOnTop, 'floating');
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

  setGeometry(geometry: PetGeometry): void {
    this.geometry = geometry;
  }

  /** 气泡出现 / 消失时刷新命中区域，让气泡上的点击也能落到桌宠窗口。 */
  setBubbleVisible(visible: boolean): void {
    if (!visible) this.geometry.bubble = null;
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
    // 窗口宽度 = 猫宽 + 气泡留白，缩放时用窗口宽度反推猫本体尺寸
    const size = petSizeFromWindowWidth(bounds.width);
    const clamped = clampPosition({ x: bounds.x, y: bounds.y }, size);
    win.setPosition(clamped.x, clamped.y, false);
    this.onDrop(clamped);
    win.webContents.send(IPC.dropped);
    // 松手后立即重算一次穿透状态
    this.evaluateHit();
  }

  send(channel: string, payload?: unknown): void {
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    win.webContents.send(channel, payload);
  }

  destroy(): void {
    this.stopTimers();
    if (this.win && !this.win.isDestroyed()) this.win.destroy();
    this.win = null;
  }

  private startHitTest(): void {
    if (this.hitTimer) return;
    this.hitTimer = setInterval(() => this.evaluateHit(), 90);
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
      // 光标不在窗口内时保持可穿透，不再抢占桌面点击
      if (!this.ignoreMouse) this.applyIgnore(true);
      return;
    }
    const localX = cursor.x - bounds.x;
    const localY = cursor.y - bounds.y;
    const overCat = pointInEllipse(localX, localY, this.geometry.cat, 0.06);
    const overBubble = this.geometry.bubble
      ? pointInRect(localX, localY, this.geometry.bubble, 6)
      : false;
    this.applyIgnore(!(overCat || overBubble));
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

export function cursorPoint(): Point {
  return screen.getCursorScreenPoint();
}
