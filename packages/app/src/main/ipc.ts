import { ipcMain, screen } from 'electron';
import { IPC } from '../shared/ipc';
import type { PetGeometry, Point, Settings } from '../shared/types';
import type { PetApp } from './app-context';

/** 只暴露必要的 IPC 能力，渲染进程拿不到 Node。 */
export function registerIpc(app: PetApp): void {
  ipcMain.handle(IPC.getSnapshot, () => app.snapshot());
  ipcMain.handle(IPC.getAppInfo, () => ({
    version: app.snapshot().version,
    platform: process.platform,
    statePath: app.store.path,
  }));
  ipcMain.handle(IPC.click, () => app.registerClick());
  ipcMain.handle(IPC.pet, () => app.petHead());
  ipcMain.handle(IPC.feed, () => app.feed('pet'));
  ipcMain.handle(IPC.updateSettings, (_event, patch: Partial<Settings>) => app.updateSettings(patch ?? {}));
  ipcMain.handle(IPC.resetSettings, () => app.resetSettings());

  ipcMain.on(IPC.ready, () => {
    app.pet.send(IPC.stateChanged, app.snapshot());
  });

  ipcMain.on(IPC.reportGeometry, (_event, geometry: PetGeometry) => {
    if (geometry?.cat) app.pet.setGeometry(geometry);
  });

  ipcMain.on(IPC.dragStart, () => app.pet.startDrag());
  ipcMain.on(IPC.dragEnd, () => app.pet.endDrag());

  ipcMain.on(IPC.contextMenu, (_event, payload: Point) => {
    const point =
      payload && Number.isFinite(payload.x) && Number.isFinite(payload.y)
        ? payload
        : screen.getCursorScreenPoint();
    app.openMenu(point);
  });

  ipcMain.on(IPC.openSettings, () => app.openSettings());
  ipcMain.on(IPC.closeSettings, () => app.closeSettings());
  ipcMain.on(IPC.hidePet, () => app.hidePet());
  ipcMain.on(IPC.quit, () => app.quit());
  ipcMain.on(IPC.menuAction, (_event, action: string) => app.handleMenuAction(action));
  ipcMain.on(IPC.closeMenu, () => app.closeMenu());
}
