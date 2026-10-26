import { screen, type Display } from 'electron';
import { clamp, type Point, type Rect } from '../shared/types';

/** 气泡区域高度（窗口比猫本体高出的部分） */
export const BUBBLE_SPACE = 96;
/** 气泡两侧额外留白，让中文短句尽量一行显示 */
export const BUBBLE_SIDE = 72;
/** 窗口底部留白 */
export const PET_MARGIN_BOTTOM = 8;
/** 距离屏幕边缘的默认留白 */
export const EDGE_MARGIN = 28;
/** 拖动时至少保留在屏幕内的高度 */
export const KEEP_VISIBLE = 56;

export function petWindowSize(size: number): { width: number; height: number } {
  return { width: size + BUBBLE_SIDE, height: size + BUBBLE_SPACE };
}

/** 由窗口宽度反推猫本体尺寸（用于拖动结束时的边界约束）。 */
export function petSizeFromWindowWidth(width: number): number {
  return clamp(width - BUBBLE_SIDE, 100, 320);
}

export function displayFor(point: Point): Display {
  return screen.getDisplayNearestPoint({ x: Math.round(point.x), y: Math.round(point.y) });
}

/** 默认停靠：主屏工作区右下角。 */
export function defaultPosition(size: number): Point {
  const { workArea } = screen.getPrimaryDisplay();
  const { width, height } = petWindowSize(size);
  return {
    x: workArea.x + workArea.width - width - EDGE_MARGIN,
    y: workArea.y + workArea.height - height - PET_MARGIN_BOTTOM,
  };
}

/** 约束窗口位置：整只猫不会完全跑出屏幕。 */
export function clampPosition(position: Point, size: number): Point {
  const { width, height } = petWindowSize(size);
  const display = displayFor(position);
  const { workArea } = display;
  const minX = workArea.x - width + KEEP_VISIBLE;
  const maxX = workArea.x + workArea.width - KEEP_VISIBLE;
  const minY = workArea.y;
  const maxY = workArea.y + workArea.height - KEEP_VISIBLE;
  return {
    x: Math.round(clamp(position.x, minX, maxX)),
    y: Math.round(clamp(position.y, minY, maxY)),
  };
}

export function pointInRect(x: number, y: number, rect: Rect, padding = 0): boolean {
  return (
    x >= rect.x - padding &&
    x <= rect.x + rect.width + padding &&
    y >= rect.y - padding &&
    y <= rect.y + rect.height + padding
  );
}

/** 圆形贴纸用椭圆命中测试，手感比矩形更自然。 */
export function pointInEllipse(x: number, y: number, rect: Rect, grow = 0.04): boolean {
  if (rect.width <= 0 || rect.height <= 0) return false;
  const cx = rect.x + rect.width / 2;
  const cy = rect.y + rect.height / 2;
  const rx = (rect.width / 2) * (1 + grow);
  const ry = (rect.height / 2) * (1 + grow);
  const dx = (x - cx) / rx;
  const dy = (y - cy) / ry;
  return dx * dx + dy * dy <= 1;
}
