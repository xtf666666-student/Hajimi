import { readDshSnapshot, dshCachePath, type DshSession, type DshSnapshot } from './dsh-bridge';
import { relativeTime, shortTitle } from '../shared/phrases';
import type { DshView, PetSnapshot, Settings } from '../shared/types';
import { DEFAULT_SETTINGS, clamp, SIZE_MAX, SIZE_MIN } from '../shared/types';
import { join } from 'node:path';
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { app } from 'electron';

interface PetState {
  settings: Settings;
  position: { x: number; y: number } | null;
}

function toView(snapshot: DshSnapshot, now = Date.now()): DshView {
  return {
    available: snapshot.available,
    activeCount: snapshot.activeCount,
    total: snapshot.sessions.length,
    error: snapshot.error,
    focusTitle: snapshot.focusTitle,
    sessions: snapshot.sessions.map((session: DshSession) => ({
      id: session.id,
      title: shortTitle(session.title, 26),
      active: session.active,
      turn: session.turn,
      ago: relativeTime(session.updatedAt, now),
    })),
  };
}

/** 妗屽疇鏈湴鐘舵€?+ DSH 浼氳瘽浠ｇ悊銆?*/
export class PetContext {
  private state: PetState;
  private readonly file: string;
  private cached: { value: DshSnapshot; at: number } | null = null;
  private saveTimer: NodeJS.Timeout | null = null;

  constructor(filePath?: string) {
    this.file = filePath ?? join(app.getPath('userData'), 'pet-state.json');
    this.state = this.load();
  }

  get path(): string {
    return this.file;
  }

  get dshPath(): string {
    return dshCachePath();
  }

  private load(): PetState {
    const fallback: PetState = { settings: { ...DEFAULT_SETTINGS }, position: null };
    try {
      if (!existsSync(this.file)) return fallback;
      const raw = JSON.parse(readFileSync(this.file, 'utf8')) as Partial<PetState>;
      const settings = (raw.settings ?? {}) as Partial<Settings>;
      const position = raw.position;
      return {
        settings: {
          size: clamp(Math.round(Number(settings.size) || DEFAULT_SETTINGS.size), SIZE_MIN, SIZE_MAX),
          visible: typeof settings.visible === 'boolean' ? settings.visible : true,
          animations:
            typeof settings.animations === 'boolean' ? settings.animations : DEFAULT_SETTINGS.animations,
        },
        position:
          position && Number.isFinite(position.x) && Number.isFinite(position.y)
            ? { x: Math.round(position.x), y: Math.round(position.y) }
            : null,
      };
    } catch {
      return fallback;
    }
  }

  get settings(): Settings {
    return this.state.settings;
  }

  get position(): { x: number; y: number } | null {
    return this.state.position;
  }

  patchSettings(patch: Partial<Settings>): Settings {
    const next: Settings = { ...this.state.settings };
    if (patch.size !== undefined) next.size = clamp(Math.round(patch.size), SIZE_MIN, SIZE_MAX);
    if (patch.visible !== undefined) next.visible = Boolean(patch.visible);
    if (patch.animations !== undefined) next.animations = Boolean(patch.animations);
    this.state.settings = next;
    this.scheduleSave();
    return next;
  }

  setPosition(position: { x: number; y: number }): void {
    this.state.position = { x: Math.round(position.x), y: Math.round(position.y) };
    this.scheduleSave();
  }

  /** 浼氳瘽鎵弿甯?1.2 绉掔紦瀛橈紝閬垮厤姣忔鐐瑰嚮閮借鐩樸€?*/
  dsh(force = false): DshSnapshot {
    const now = Date.now();
    if (!force && this.cached && now - this.cached.at < 1200) return this.cached.value;
    const value = readDshSnapshot();
    this.cached = { value, at: now };
    return value;
  }

  snapshot(force = false): PetSnapshot {
    return {
      settings: { ...this.state.settings },
      dsh: toView(this.dsh(force)),
      version: app.getVersion(),
    };
  }

  flush(): void {
    if (this.saveTimer) {
      clearTimeout(this.saveTimer);
      this.saveTimer = null;
    }
    try {
      mkdirSync(app.getPath('userData'), { recursive: true });
      const tmp = `${this.file}.tmp`;
      writeFileSync(tmp, JSON.stringify(this.state, null, 2), 'utf8');
      renameSync(tmp, this.file);
    } catch (error) {
      console.warn('[pet] 淇濆瓨鏈湴鐘舵€佸け璐ワ細', error);
    }
  }

  private scheduleSave(): void {
    if (this.saveTimer) return;
    this.saveTimer = setTimeout(() => {
      this.saveTimer = null;
      this.flush();
    }, 200);
    this.saveTimer.unref?.();
  }
}







