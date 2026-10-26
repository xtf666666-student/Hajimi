import { app } from 'electron';
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import {
  clamp,
  createStats,
  DEFAULT_SETTINGS,
  HUNGER_MAX,
  OPACITY_MIN,
  SIZE_MAX,
  SIZE_MIN,
  STATE_VERSION,
  type PersistedState,
  type Point,
  type Settings,
  type Stats,
} from '../shared/types';

function asNumber(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function asBoolean(value: unknown, fallback: boolean): boolean {
  return typeof value === 'boolean' ? value : fallback;
}

/** 本地 JSON 持久化：设置、统计、饱食度、窗口位置。写入去抖 + 原子替换。 */
export class Store {
  private readonly file: string;
  private state: PersistedState;
  private timer: NodeJS.Timeout | null = null;

  constructor(filePath?: string) {
    this.file = filePath ?? join(app.getPath('userData'), 'state.json');
    this.state = this.load();
  }

  get path(): string {
    return this.file;
  }

  private defaults(): PersistedState {
    return {
      version: STATE_VERSION,
      settings: { ...DEFAULT_SETTINGS },
      stats: createStats(),
      hunger: 70,
      lastFedAt: 0,
      position: null,
    };
  }

  private load(): PersistedState {
    const fallback = this.defaults();
    try {
      if (!existsSync(this.file)) return fallback;
      const raw = JSON.parse(readFileSync(this.file, 'utf8')) as Partial<PersistedState>;
      const s = (raw.settings ?? {}) as Partial<Settings>;
      const stats = (raw.stats ?? {}) as Partial<Stats>;
      const position = raw.position;
      return {
        version: STATE_VERSION,
        settings: {
          size: clamp(Math.round(asNumber(s.size, fallback.settings.size)), SIZE_MIN, SIZE_MAX),
          opacity: clamp(asNumber(s.opacity, fallback.settings.opacity), OPACITY_MIN, 1),
          autoSpeak: asBoolean(s.autoSpeak, fallback.settings.autoSpeak),
          animations: asBoolean(s.animations, fallback.settings.animations),
          alwaysOnTop: asBoolean(s.alwaysOnTop, fallback.settings.alwaysOnTop),
        },
        stats: {
          clicks: Math.max(0, Math.round(asNumber(stats.clicks, 0))),
          pets: Math.max(0, Math.round(asNumber(stats.pets, 0))),
          feeds: Math.max(0, Math.round(asNumber(stats.feeds, 0))),
          firstSeenAt: asNumber(stats.firstSeenAt, fallback.stats.firstSeenAt),
          lastActiveAt: asNumber(stats.lastActiveAt, fallback.stats.lastActiveAt),
        },
        hunger: clamp(asNumber(raw.hunger, fallback.hunger), 0, HUNGER_MAX),
        lastFedAt: Math.max(0, asNumber(raw.lastFedAt, 0)),
        position:
          position && Number.isFinite(position.x) && Number.isFinite(position.y)
            ? { x: Math.round(position.x), y: Math.round(position.y) }
            : null,
      };
    } catch (error) {
      console.warn('[store] 读取本地状态失败，使用默认值：', error);
      return fallback;
    }
  }

  getState(): PersistedState {
    return this.state;
  }

  get settings(): Settings {
    return this.state.settings;
  }

  get stats(): Stats {
    return this.state.stats;
  }

  get hunger(): number {
    return this.state.hunger;
  }

  patchSettings(patch: Partial<Settings>): Settings {
    const next: Settings = { ...this.state.settings };
    if (patch.size !== undefined) next.size = clamp(Math.round(patch.size), SIZE_MIN, SIZE_MAX);
    if (patch.opacity !== undefined) next.opacity = clamp(patch.opacity, OPACITY_MIN, 1);
    if (patch.autoSpeak !== undefined) next.autoSpeak = Boolean(patch.autoSpeak);
    if (patch.animations !== undefined) next.animations = Boolean(patch.animations);
    if (patch.alwaysOnTop !== undefined) next.alwaysOnTop = Boolean(patch.alwaysOnTop);
    this.state.settings = next;
    this.touch();
    this.scheduleSave();
    return next;
  }

  resetSettings(): Settings {
    this.state.settings = { ...DEFAULT_SETTINGS };
    this.touch();
    this.scheduleSave();
    return this.state.settings;
  }

  addStats(kind: 'clicks' | 'pets' | 'feeds'): Stats {
    this.state.stats[kind] += 1;
    this.touch();
    this.scheduleSave();
    return this.state.stats;
  }

  feed(): { hunger: number; stats: Stats } {
    const now = Date.now();
    this.state.hunger = clamp(this.state.hunger + 18, 0, HUNGER_MAX);
    this.state.lastFedAt = now;
    this.state.stats.feeds += 1;
    this.touch();
    this.scheduleSave();
    return { hunger: this.state.hunger, stats: this.state.stats };
  }

  decayHunger(amount: number): number {
    const next = clamp(this.state.hunger - amount, 0, HUNGER_MAX);
    if (next !== this.state.hunger) {
      this.state.hunger = next;
      this.touch();
      this.scheduleSave();
    }
    return this.state.hunger;
  }

  setPosition(position: Point): void {
    this.state.position = { x: Math.round(position.x), y: Math.round(position.y) };
    this.scheduleSave();
  }

  private touch(): void {
    this.state.stats.lastActiveAt = Date.now();
  }

  private scheduleSave(): void {
    if (this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      this.flush();
    }, 250);
    this.timer.unref?.();
  }

  flush(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    try {
      mkdirSync(dirname(this.file), { recursive: true });
      const tmp = `${this.file}.tmp`;
      writeFileSync(tmp, JSON.stringify(this.state, null, 2), 'utf8');
      renameSync(tmp, this.file);
    } catch (error) {
      console.warn('[store] 写入本地状态失败：', error);
    }
  }
}
