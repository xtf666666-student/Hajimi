import { screen, shell, type Display } from 'electron';
import { clamp, type Settings } from '../shared/types';

/** 窗口比猫高一截，留给头顶的对话计数气泡。 */
export const BADGE_HEIGHT = 30;
export const BADGE_SIDE = 22;
/** 展开对话列表时需要额外的高度与宽度 */
export const LIST_HEIGHT = 196;
export const LIST_WIDTH = 214;

export function petWindowSize(size: number, listOpen = false): { width: number; height: number } {
  const baseW = Math.round(size * 0.82) + BADGE_SIDE;
  const baseH = size + BADGE_HEIGHT;
  if (!listOpen) return { width: baseW, height: baseH };
  return {
    width: Math.max(baseW, LIST_WIDTH),
    height: baseH + LIST_HEIGHT,
  };
}

/** 素材是 376x460，宽高比 0.817 */
export const PET_ASPECT = 376 / 460;

export function displayFor(position: { x: number; y: number }): Display {
  return screen.getDisplayNearestPoint({ x: Math.round(position.x), y: Math.round(position.y) });
}

/** 默认停靠：主屏工作区右下角，离任务栏一点点距离。 */
export function defaultPosition(size: number): { x: number; y: number } {
  const { workArea } = screen.getPrimaryDisplay();
  const { width, height } = petWindowSize(size);
  return {
    x: workArea.x + workArea.width - width - 24,
    y: workArea.y + workArea.height - height - 6,
  };
}

/** 整只猫不能被拖出屏幕太多。 */
export function clampPosition(position: { x: number; y: number }, size: number): { x: number; y: number } {
  const { width, height } = petWindowSize(size);
  const display = displayFor(position);
  const { workArea } = display;
  const keep = 34;
  return {
    x: Math.round(clamp(position.x, workArea.x - width + keep, workArea.x + workArea.width - keep)),
    y: Math.round(clamp(position.y, workArea.y - 8, workArea.y + workArea.height - keep)),
  };
}

/** 椭圆命中测试：透明角落不该吃掉桌面点击。 */
export function pointInEllipse(
  x: number,
  y: number,
  rect: { x: number; y: number; width: number; height: number },
  grow = 0.05,
): boolean {
  if (rect.width <= 0 || rect.height <= 0) return false;
  const cx = rect.x + rect.width / 2;
  const cy = rect.y + rect.height / 2;
  const rx = (rect.width / 2) * (1 + grow);
  const ry = (rect.height / 2) * (1 + grow);
  const dx = (x - cx) / rx;
  const dy = (y - cy) / ry;
  return dx * dx + dy * dy <= 1;
}

export function pointInRect(
  x: number,
  y: number,
  rect: { x: number; y: number; width: number; height: number },
  padding = 0,
): boolean {
  return (
    x >= rect.x - padding &&
    x <= rect.x + rect.width + padding &&
    y >= rect.y - padding &&
    y <= rect.y + rect.height + padding
  );
}

/** 唤出 DSH。deep link 已由 DSH 注册，失败就退回启动主程序。 */
export function focusDsh(): boolean {
  const results: string[] = [];
  try {
    results.push(String(shell.openExternal('dsh://open')));
  } catch (error) {
    results.push(String(error));
  }
  return results.every((entry) => !entry.includes('Error'));
}

export function settingsEqual(a: Settings, b: Settings): boolean {
  return a.size === b.size && a.visible === b.visible && a.animations === b.animations;
}
