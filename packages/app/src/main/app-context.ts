import { app, Tray, Menu, nativeImage, shell, screen } from 'electron';
import { existsSync } from 'node:fs';
import { IPC } from '../shared/ipc';
import { IDLE_TALK_LINES, pick } from '../shared/phrases';
import {
  FEED_HUNGER_GAIN,
  HUNGER_DECAY_AMOUNT,
  HUNGER_DECAY_INTERVAL_MS,
  SIZE_PRESETS,
  clamp,
  type PersistedState,
  type PetSnapshot,
  type Point,
  type Settings,
} from '../shared/types';
import { MenuWindow } from './menu-window';
import { assetPath } from './paths';
import { PetWindow } from './pet-window';
import { SettingsWindow } from './settings-window';
import { Store } from './store';

/** 应用级协调器：窗口、托盘、随机说话、设置广播都在这里收口。 */
export class PetApp {
  readonly store = new Store();
  readonly pet: PetWindow;
  readonly settings = new SettingsWindow();
  readonly menu = new MenuWindow();

  private tray: Tray | null = null;
  private talkTimer: NodeJS.Timeout | null = null;
  private decayTimer: NodeJS.Timeout | null = null;
  private quitting = false;

  constructor() {
    this.pet = new PetWindow((position: Point) => this.store.setPosition(position));
  }

  start(): void {
    const state = this.store.getState();
    this.pet.create(state.settings, state.position);
    this.createTray();
    this.scheduleTalk();
    this.startHungerDecay();
  }

  snapshot(): PetSnapshot {
    const state = this.store.getState();
    return {
      settings: { ...state.settings },
      stats: { ...state.stats },
      hunger: state.hunger,
      visible: this.pet.isVisible,
      version: app.getVersion(),
      platform: process.platform,
    };
  }

  broadcast(): void {
    const snapshot = this.snapshot();
    this.pet.send(IPC.stateChanged, snapshot);
    this.settings.send(IPC.stateChanged, snapshot);
    this.refreshTray();
  }

  openSettings(): void {
    this.settings.open(this.snapshot());
  }

  closeSettings(): void {
    this.settings.close();
  }

  openMenu(at: Point): void {
    this.menu.open(at);
  }

  closeMenu(): void {
    this.menu.close();
  }

  hidePet(): void {
    this.pet.hide();
    this.menu.close();
    this.broadcast();
  }

  showPet(): void {
    this.pet.show();
    this.broadcast();
  }

  togglePet(): void {
    if (this.pet.isVisible) this.hidePet();
    else this.showPet();
  }

  feed(source: 'pet' | 'settings' | 'menu' | 'tray' = 'menu'): PetSnapshot {
    this.store.feed();
    // 渲染进程自己发起的喂食已经在放动画，不再回推命令，避免命令回环
    if (source !== 'pet') this.pet.send(IPC.command, { type: 'feed', source });
    const snapshot = this.snapshot();
    this.broadcast();
    return snapshot;
  }

  petHead(): PetSnapshot {
    this.store.addStats('pets');
    const snapshot = this.snapshot();
    this.broadcast();
    return snapshot;
  }

  registerClick(): PetSnapshot {
    this.store.addStats('clicks');
    const snapshot = this.snapshot();
    this.broadcast();
    return snapshot;
  }

  updateSettings(patch: Partial<Settings>): PetSnapshot {
    const next = this.store.patchSettings(patch);
    this.pet.applySettings(next);
    if (patch.size !== undefined) this.pet.send(IPC.command, { type: 'resize', size: next.size });
    if (patch.autoSpeak !== undefined) this.scheduleTalk();
    const snapshot = this.snapshot();
    this.broadcast();
    return snapshot;
  }

  resetSettings(): PetSnapshot {
    const next = this.store.resetSettings();
    this.pet.applySettings(next);
    this.pet.send(IPC.command, { type: 'resize', size: next.size });
    const snapshot = this.snapshot();
    this.broadcast();
    return snapshot;
  }

  cycleSize(): PetSnapshot {
    const current = this.store.settings.size;
    const presets = SIZE_PRESETS.map((preset) => preset.value);
    const nextPreset = presets.find((value) => value > current) ?? presets[0];
    return this.updateSettings({ size: nextPreset });
  }

  handleMenuAction(action: string): void {
    this.menu.close();
    switch (action) {
      case 'pet':
        // 统计在渲染进程回调时累加，避免双计数
        this.pet.send(IPC.command, { type: 'pet-head', source: 'menu' });
        break;
      case 'feed':
        this.feed('menu');
        break;
      case 'cycle-size':
        this.cycleSize();
        this.pet.send(IPC.command, { type: 'size-toast', size: this.store.settings.size });
        break;
      case 'hide':
        this.hidePet();
        break;
      case 'settings':
        this.openSettings();
        break;
      case 'quit':
        this.quit();
        break;
      default:
        break;
    }
  }

  say(text?: string): void {
    if (!this.store.settings.autoSpeak) return;
    if (!this.pet.isVisible) return;
    this.pet.send(IPC.say, { text: text ?? pick(IDLE_TALK_LINES) });
  }

  quit(): void {
    if (this.quitting) return;
    this.quitting = true;
    this.stopTimers();
    this.store.flush();
    this.menu.destroy();
    this.settings.destroy();
    this.tray?.destroy();
    this.tray = null;
    this.pet.destroy();
    app.quit();
  }

  dispose(): void {
    this.stopTimers();
    this.store.flush();
  }

  private createTray(): void {
    const iconFile = assetPath('tray.png');
    const icon = nativeImage.createFromPath(iconFile);
    const tray = new Tray(icon.isEmpty() ? nativeImage.createEmpty() : icon);
    tray.setToolTip('哈基米桌宠');
    tray.on('click', () => this.togglePet());
    tray.on('double-click', () => this.showPet());
    this.tray = tray;
    this.refreshTray();
  }

  private refreshTray(): void {
    const tray = this.tray;
    if (!tray || tray.isDestroyed()) return;
    const menu = Menu.buildFromTemplate([
      { label: '显示 / 隐藏哈基米', click: () => this.togglePet() },
      { label: '摸摸哈基米', click: () => this.handleMenuAction('pet') },
      { label: '喂食', click: () => this.feed('tray') },
      { type: 'separator' },
      { label: '打开设置', click: () => this.openSettings() },
      { type: 'separator' },
      { label: '退出桌宠', click: () => this.quit() },
    ]);
    tray.setContextMenu(menu);
  }

  private scheduleTalk(): void {
    this.stopTalkTimer();
    if (!this.store.settings.autoSpeak) return;
    const delay = 70_000 + Math.random() * 110_000;
    this.talkTimer = setTimeout(() => {
      if (Math.random() < 0.55) this.say();
      this.scheduleTalk();
    }, delay);
    this.talkTimer.unref?.();
  }

  private startHungerDecay(): void {
    this.decayTimer = setInterval(() => {
      const before = this.store.hunger;
      const after = this.store.decayHunger(HUNGER_DECAY_AMOUNT);
      if (after !== before) this.broadcast();
    }, HUNGER_DECAY_INTERVAL_MS);
    this.decayTimer.unref?.();
  }

  private stopTalkTimer(): void {
    if (this.talkTimer) {
      clearTimeout(this.talkTimer);
      this.talkTimer = null;
    }
  }

  private stopTimers(): void {
    this.stopTalkTimer();
    if (this.decayTimer) {
      clearInterval(this.decayTimer);
      this.decayTimer = null;
    }
  }
}

export function hungerLabel(hunger: number): string {
  if (hunger >= 85) return '撑到了';
  if (hunger >= 60) return '很满足';
  if (hunger >= 35) return '还算饱';
  if (hunger >= 15) return '有点饿';
  return '饿扁了';
}

export function hungerGain(): number {
  return FEED_HUNGER_GAIN;
}

export function clampSize(size: number): number {
  return clamp(size, 100, 320);
}
