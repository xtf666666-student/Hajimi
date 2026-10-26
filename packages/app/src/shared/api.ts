import type { MenuAction, PetGeometry, PetSnapshot, Point, Settings } from './types';

/** preload 暴露给渲染进程的唯一 API 面。 */
export interface HajimiApi {
  getSnapshot(): Promise<PetSnapshot>;
  getAppInfo(): Promise<{ version: string; platform: string; statePath: string }>;

  reportGeometry(geometry: PetGeometry): void;
  dragStart(): void;
  dragEnd(): void;

  registerClick(): Promise<PetSnapshot>;
  petHead(): Promise<PetSnapshot>;
  feed(): Promise<PetSnapshot>;

  updateSettings(patch: Partial<Settings>): Promise<PetSnapshot>;
  resetSettings(): Promise<PetSnapshot>;

  showContextMenu(point: Point): void;
  openSettings(): void;
  closeSettings(): void;
  hidePet(): void;
  quit(): void;

  menuAction(action: MenuAction): void;
  closeMenu(): void;

  onState(listener: (snapshot: PetSnapshot) => void): () => void;
  onSay(listener: (payload: { text: string }) => void): () => void;
  onCommand(listener: (payload: { type: string; [key: string]: unknown }) => void): () => void;
  onDropped(listener: () => void): () => void;
}

declare global {
  interface Window {
    hajimi: HajimiApi;
  }
}
