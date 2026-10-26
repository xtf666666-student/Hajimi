/**
 * 桌宠共享类型定义：主进程、预加载脚本与渲染进程共用。
 */

export type Emotion = 'idle' | 'happy' | 'confused' | 'angry' | 'sleepy' | 'surprised';

export interface Settings {
  /** 桌宠显示尺寸（CSS 像素，正方形） */
  size: number;
  /** 窗口整体不透明度 0.35 - 1 */
  opacity: number;
  /** 是否允许哈基米主动说话 */
  autoSpeak: boolean;
  /** 是否播放动画 */
  animations: boolean;
  /** 是否始终置顶 */
  alwaysOnTop: boolean;
}

export const DEFAULT_SETTINGS: Settings = {
  size: 200,
  opacity: 1,
  autoSpeak: true,
  animations: true,
  alwaysOnTop: true,
};

export const SIZE_PRESETS = [
  { label: '小', value: 140 },
  { label: '中', value: 200 },
  { label: '大', value: 280 },
] as const;

export const SIZE_MIN = 100;
export const SIZE_MAX = 320;
export const OPACITY_MIN = 0.35;

export interface Stats {
  clicks: number;
  pets: number;
  feeds: number;
  firstSeenAt: number;
  lastActiveAt: number;
}

export function createStats(now = Date.now()): Stats {
  return { clicks: 0, pets: 0, feeds: 0, firstSeenAt: now, lastActiveAt: now };
}

export interface Point {
  x: number;
  y: number;
}

export interface PersistedState {
  version: number;
  settings: Settings;
  stats: Stats;
  /** 饱食度 0 - 100 */
  hunger: number;
  lastFedAt: number;
  /** 上次退出时的窗口位置（DIP） */
  position: Point | null;
}

export interface PetSnapshot {
  settings: Settings;
  stats: Stats;
  hunger: number;
  visible: boolean;
  version: string;
  platform: string;
}

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface PetGeometry {
  /** 猫咪本体的矩形（窗口内 CSS 像素） */
  cat: Rect;
  /** 气泡矩形，未显示时为 null */
  bubble: Rect | null;
}

export type MenuAction =
  | 'pet'
  | 'feed'
  | 'cycle-size'
  | 'hide'
  | 'settings'
  | 'quit';

export const STATE_VERSION = 1;
export const HUNGER_MAX = 100;
export const FEED_HUNGER_GAIN = 18;
/** 饱食度自然下降：每 10 分钟 -2 */
export const HUNGER_DECAY_INTERVAL_MS = 10 * 60 * 1000;
export const HUNGER_DECAY_AMOUNT = 2;

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
