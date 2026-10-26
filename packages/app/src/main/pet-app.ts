import { app, clipboard, shell, Tray, Menu, nativeImage } from 'electron';
import { IPC } from '../shared/ipc';
import { BUSY_LINES, CLICK_LINES, IDLE_LINES, pick } from '../shared/phrases';
import {
  DEFAULT_SETTINGS,
  SIZE_MAX,
  SIZE_MIN,
  SIZE_STEP,
  clamp,
  type MenuAction,
  type PetSnapshot,
  type Settings,
} from '../shared/types';
import { focusDsh } from './layout';
import { MenuWindow } from './menu-window';
import { assetPath } from './paths';
import { PetContext } from './pet-context';
import { PetWindow } from './pet-window';

/** 应用协调器：窗口、托盘、会话刷新、说话调度。 */
export class PetApp {
  readonly context = new PetContext();
  readonly pet: PetWindow;
  readonly menu = new MenuWindow();

  private tray: Tray | null = null;
  private pollTimer: NodeJS.Timeout | null = null;
  private talkTimer: NodeJS.Timeout | null = null;
  private quitting = false;
  private lastActiveCount = -1;

  constructor() {
    this.pet = new PetWindow((position) => this.context.setPosition(position));
  }

  start(): void {
    const settings = this.context.settings;
    this.pet.create(settings, this.context.position);
    if (!settings.visible) this.pet.hide();
    this.createTray();
    this.startPolling();
    this.scheduleTalk();
  }

  snapshot(force = false): PetSnapshot {
    return this.context.snapshot(force);
  }

  broadcast(force = false): PetSnapshot {
    const snapshot = this.snapshot(force);
    this.pet.broadcast(snapshot);
    this.menu.send(IPC.state, snapshot);
    this.refreshTray();
    return snapshot;
  }

  registerClick(): PetSnapshot {
    const snapshot = this.snapshot();
    const mood = snapshot.dsh.activeCount > 0 ? 'alert' : 'curious';
    const lines = snapshot.dsh.activeCount > 0 ? CLICK_LINES.alert : CLICK_LINES.curious;
    this.pet.send(IPC.command, { type: 'say', text: pick(lines), mood });
    return snapshot;
  }

  petHead(): PetSnapshot {
    this.pet.send(IPC.command, { type: 'pet' });
    return this.snapshot();
  }

  /** 唤出 DeepSeek Harness。 */
  openDsh(): void {
    const ok = focusDsh();
    const view = this.context.dsh(true);
    const text = ok
      ? view.activeCount > 0
        ? `DSH 打开了，有 ${view.activeCount} 个在跑。`
        : 'DSH 打开了。'
      : '没找到 DSH 入口，请手动打开。';
    this.pet.send(IPC.command, {
      type: 'say',
      text,
      mood: ok ? 'excited' : 'annoyed',
    });
    this.broadcast(true);
  }

  /**
   * 尝试进入指定对话：
   * DSH 只注册了 dsh://open（聚焦主窗口），没有逐会话深链接，
   * 所以这里唤出 DSH 并把对话标题放进剪贴板，用户可以直接粘贴搜索。
   */
  openSession(sessionId: string): void {
    const view = this.context.dsh(true);
    const session = view.sessions.find((item) => item.id === sessionId);
    focusDsh();
    if (session) {
      clipboard.writeText(session.title);
      this.pet.send(IPC.command, {
        type: 'say',
        text: `已唤出 DSH，标题在剪贴板：${session.title}`,
        mood: 'excited',
      });
    } else {
      this.pet.send(IPC.command, { type: 'say', text: '这个对话找不到了。', mood: 'annoyed' });
    }
    this.broadcast(true);
  }

  updateSettings(patch: Partial<Settings>): PetSnapshot {
    const next = this.context.patchSettings(patch);
    this.pet.applySettings(next);
    if (patch.visible !== undefined) {
      if (next.visible) this.pet.show();
      else this.pet.hide();
    }
    if (patch.animations !== undefined) {
      this.pet.send(IPC.command, { type: 'anim', enabled: next.animations });
    }
    return this.broadcast(true);
  }

  cycleSize(): PetSnapshot {
    const current = this.context.settings.size;
    const steps = [72, 88, 104, 128, 156];
    const next = steps.find((value) => value > current) ?? steps[0];
    return this.updateSettings({ size: clamp(next, SIZE_MIN, SIZE_MAX) });
  }

  resetSettings(): PetSnapshot {
    const snapshot = this.updateSettings({ ...DEFAULT_SETTINGS });
    return snapshot;
  }

  handleMenuAction(action: MenuAction): void {
    this.menu.close();
    switch (action) {
      case 'open-dsh':
        this.openDsh();
        break;
      case 'refresh':
        this.broadcast(true);
        this.pet.send(IPC.command, { type: 'refresh-pulse' });
        break;
      case 'size':
        this.cycleSize();
        break;
      case 'toggle-anim':
        this.updateSettings({ animations: !this.context.settings.animations });
        break;
      case 'hide':
        this.updateSettings({ visible: false });
        break;
      case 'quit':
        this.quit();
        break;
      default:
        break;
    }
  }

  openMenu(at: { x: number; y: number }): void {
    this.menu.open(at, this.snapshot());
  }

  quit(): void {
    if (this.quitting) return;
    this.quitting = true;
    this.stopTimers();
    this.context.flush();
    this.menu.destroy();
    this.tray?.destroy();
    this.tray = null;
    this.pet.destroy();
    app.quit();
  }

  dispose(): void {
    this.stopTimers();
    this.context.flush();
  }

  private createTray(): void {
    const icon = nativeImage.createFromPath(assetPath('tray.png'));
    const tray = new Tray(icon.isEmpty() ? nativeImage.createEmpty() : icon);
    tray.setToolTip('哈基米 · DeepSeek Harness 桌宠');
    tray.on('click', () => this.toggleVisible());
    this.tray = tray;
    this.refreshTray();
  }

  private toggleVisible(): void {
    const next = !this.context.settings.visible;
    this.updateSettings({ visible: next });
  }

  private refreshTray(): void {
    const tray = this.tray;
    if (!tray || tray.isDestroyed()) return;
    const snapshot = this.snapshot();
    const menu = Menu.buildFromTemplate([
      { label: `进行中：${snapshot.dsh.activeCount} 个对话`, enabled: false },
      { label: `共 ${snapshot.dsh.total} 个对话`, enabled: false },
      { type: 'separator' },
      { label: '打开 DeepSeek Harness', click: () => this.openDsh() },
      { label: '刷新对话状态', click: () => this.handleMenuAction('refresh') },
      { label: '切换大小', click: () => this.cycleSize() },
      { label: snapshot.settings.visible ? '隐藏哈基米' : '显示哈基米', click: () => this.toggleVisible() },
      { type: 'separator' },
      { label: '退出', click: () => this.quit() },
    ]);
    tray.setContextMenu(menu);
  }

  /** 轮询 DSH 会话缓存：状态变化时立刻推给渲染层。 */
  private startPolling(): void {
    this.pollTimer = setInterval(() => {
      const view = this.context.dsh(true);
      if (view.activeCount !== this.lastActiveCount) {
        this.lastActiveCount = view.activeCount;
        this.broadcast(true);
      }
    }, 4000);
    this.pollTimer.unref?.();
  }

  /** 偶尔主动说一句，默认很安静。 */
  private scheduleTalk(): void {
    this.stopTalk();
    const delay = 5 * 60_000 + Math.random() * 8 * 60_000;
    this.talkTimer = setTimeout(() => {
      if (!this.context.settings.visible) {
        this.scheduleTalk();
        return;
      }
      const view = this.context.dsh(true);
      if (Math.random() < 0.4) {
        const lines = view.activeCount > 0 ? BUSY_LINES : IDLE_LINES;
        this.pet.send(IPC.command, { type: 'say', text: pick(lines), mood: 'idle' });
      }
      this.scheduleTalk();
    }, delay);
    this.talkTimer.unref?.();
  }

  private stopTalk(): void {
    if (this.talkTimer) {
      clearTimeout(this.talkTimer);
      this.talkTimer = null;
    }
  }

  private stopTimers(): void {
    this.stopTalk();
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }
}

export { SIZE_STEP };
export { shell };
