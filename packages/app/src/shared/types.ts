/**
 * 桌宠共享类型：主进程、预加载与渲染进程共用。
 * 这一版专为 DeepSeek Harness 服务：只保留最小配置 + 对话状态。
 */

export type Mood = 'idle' | 'curious' | 'excited' | 'sleepy' | 'annoyed' | 'alert';

export interface Settings {
  /** 桌宠高度（逻辑像素）。默认约等于两个桌面图标 */
  size: number;
  /** 桌面宠物是否显示 */
  visible: boolean;
  /** 动画开关 */
  animations: boolean;
}

export const DEFAULT_SETTINGS: Settings = {
  size: 104,
  visible: true,
  animations: true,
};

export const SIZE_MIN = 72;
export const SIZE_MAX = 180;
export const SIZE_STEP = 8;

export interface SessionView {
  id: string;
  title: string;
  /** 是否正在进行中 */
  active: boolean;
  turn: number;
  /** 相对时间描述，例如「刚刚」「12 分钟前」 */
  ago: string;
}

export interface DshView {
  available: boolean;
  activeCount: number;
  total: number;
  sessions: SessionView[];
  focusTitle: string | null;
  error: string | null;
}

export interface PetSnapshot {
  settings: Settings;
  dsh: DshView;
  version: string;
}

export const STATE_VERSION = 2;

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

export type MenuAction = 'open-dsh' | 'refresh' | 'size' | 'toggle-anim' | 'hide' | 'quit';

