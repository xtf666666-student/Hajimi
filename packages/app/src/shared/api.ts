import type { MenuAction, Mood, PetSnapshot, Settings } from './types';

export interface CatRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface HajimiApi {
  getSnapshot(): Promise<PetSnapshot>;
  refresh(): Promise<PetSnapshot>;
  click(): Promise<PetSnapshot>;
  pet(): Promise<PetSnapshot>;

  /** 鍞ゅ嚭 DeepSeek Harness 涓荤獥鍙?*/
  openDsh(): void;
  /** 鎵撳紑鏌愪釜瀵硅瘽锛堝敜鍑?DSH 骞舵妸鏍囬鏀捐繘鍓创鏉匡級 */
  openSession(sessionId: string): void;

  dragStart(): void;
  dragEnd(): void;
  reportGeometry(geometry: { cat: CatRect }): void;
  /** 展开对话列表时通知主进程调整窗口大小 */
  setListOpen(open: boolean): void;

  showMenu(point: { x: number; y: number }): void;
  menuAction(action: MenuAction): void;
  closeMenu(): void;
  quit(): void;

  onState(listener: (snapshot: PetSnapshot) => void): () => void;
  onDropped(listener: () => void): () => void;
  onCommand(listener: (payload: { type: string; [key: string]: unknown }) => void): () => void;
}

export type { MenuAction, Mood, PetSnapshot, Settings };

declare global {
  interface Window {
    hajimi: HajimiApi;
  }
}

