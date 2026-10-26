import { screen, ipcMain } from 'electron';
import { IPC } from '../shared/ipc';
import type { MenuAction, Settings } from '../shared/types';
import type { PetApp } from './pet-app';

/** 只暴露这几条 IPC，渲染进程拿不到 Node。 */
export function registerIpc(app: PetApp): void {
  ipcMain.handle(IPC.snapshot, () => app.snapshot());
  ipcMain.handle(IPC.refresh, () => app.broadcast(true));
  ipcMain.handle(IPC.click, () => app.registerClick());
  ipcMain.handle(IPC.pet, () => app.petHead());

  ipcMain.on(IPC.openDsh, () => app.openDsh());
  ipcMain.on(IPC.openSession, (_event, sessionId: string) => {
    if (typeof sessionId === 'string' && sessionId.length > 0) app.openSession(sessionId);
  });

  ipcMain.on(IPC.dragStart, () => app.pet.startDrag());
  ipcMain.on(IPC.dragEnd, () => app.pet.endDrag());

  ipcMain.on(
    IPC.geometry,
    (_event, payload: { cat?: { x: number; y: number; width: number; height: number } }) => {
      if (payload?.cat) app.pet.setCatRect(payload.cat);
    },
  );

  ipcMain.on(IPC.listState, (_event, expanded: boolean) => {
    app.pet.setListExpanded(Boolean(expanded), app.context.settings.size);
  });

  ipcMain.on(IPC.menu, (_event, point: { x: number; y: number }) => {
    const safe =
      point && Number.isFinite(point.x) && Number.isFinite(point.y)
        ? point
        : screen.getCursorScreenPoint();
    app.openMenu(safe);
  });

  ipcMain.on(IPC.menuAction, (_event, action: MenuAction) => app.handleMenuAction(action));
  ipcMain.on(IPC.closeMenu, () => app.menu.close());
  ipcMain.on(IPC.quit, () => app.quit());
}

export type { Settings };
