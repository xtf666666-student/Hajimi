import { contextBridge, ipcRenderer } from 'electron';
import { IPC } from '../shared/ipc';
import type { HajimiApi } from '../shared/api';
import type { MenuAction, PetSnapshot } from '../shared/types';

function subscribe<T>(channel: string, listener: (payload: T) => void): () => void {
  const handler = (_event: Electron.IpcRendererEvent, payload: T): void => listener(payload);
  ipcRenderer.on(channel, handler);
  return () => ipcRenderer.removeListener(channel, handler);
}

const api: HajimiApi = {
  getSnapshot: () => ipcRenderer.invoke(IPC.snapshot) as Promise<PetSnapshot>,
  refresh: () => ipcRenderer.invoke(IPC.refresh) as Promise<PetSnapshot>,
  click: () => ipcRenderer.invoke(IPC.click) as Promise<PetSnapshot>,
  pet: () => ipcRenderer.invoke(IPC.pet) as Promise<PetSnapshot>,

  openDsh: () => ipcRenderer.send(IPC.openDsh),
  openSession: (sessionId: string) => ipcRenderer.send(IPC.openSession, sessionId),

  dragStart: () => ipcRenderer.send(IPC.dragStart),
  dragEnd: () => ipcRenderer.send(IPC.dragEnd),
  reportGeometry: (geometry) => ipcRenderer.send(IPC.geometry, geometry),
  setListOpen: (open: boolean) => ipcRenderer.send(IPC.listState, open),

  showMenu: (point) => ipcRenderer.send(IPC.menu, point),
  menuAction: (action: MenuAction) => ipcRenderer.send(IPC.menuAction, action),
  closeMenu: () => ipcRenderer.send(IPC.closeMenu),
  quit: () => ipcRenderer.send(IPC.quit),

  onState: (listener) => subscribe<PetSnapshot>(IPC.state, listener),
  onDropped: (listener) => subscribe<void>(IPC.dropped, () => listener()),
  onCommand: (listener) => subscribe<{ type: string; [key: string]: unknown }>(IPC.command, listener),
};

contextBridge.exposeInMainWorld('hajimi', api);

