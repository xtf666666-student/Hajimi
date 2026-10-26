import { contextBridge, ipcRenderer } from 'electron';
import { IPC } from '../shared/ipc';
import type { HajimiApi } from '../shared/api';
import type { MenuAction, PetGeometry, PetSnapshot, Point, Settings } from '../shared/types';

function subscribe<T>(channel: string, listener: (payload: T) => void): () => void {
  const handler = (_event: Electron.IpcRendererEvent, payload: T): void => listener(payload);
  ipcRenderer.on(channel, handler);
  return () => ipcRenderer.removeListener(channel, handler);
}

const api: HajimiApi = {
  getSnapshot: () => ipcRenderer.invoke(IPC.getSnapshot) as Promise<PetSnapshot>,
  getAppInfo: () =>
    ipcRenderer.invoke(IPC.getAppInfo) as Promise<{ version: string; platform: string; statePath: string }>,

  reportGeometry: (geometry: PetGeometry) => ipcRenderer.send(IPC.reportGeometry, geometry),
  dragStart: () => ipcRenderer.send(IPC.dragStart),
  dragEnd: () => ipcRenderer.send(IPC.dragEnd),

  registerClick: () => ipcRenderer.invoke(IPC.click) as Promise<PetSnapshot>,
  petHead: () => ipcRenderer.invoke(IPC.pet) as Promise<PetSnapshot>,
  feed: () => ipcRenderer.invoke(IPC.feed) as Promise<PetSnapshot>,

  updateSettings: (patch: Partial<Settings>) =>
    ipcRenderer.invoke(IPC.updateSettings, patch) as Promise<PetSnapshot>,
  resetSettings: () => ipcRenderer.invoke(IPC.resetSettings) as Promise<PetSnapshot>,

  showContextMenu: (point: Point) => ipcRenderer.send(IPC.contextMenu, point),
  openSettings: () => ipcRenderer.send(IPC.openSettings),
  closeSettings: () => ipcRenderer.send(IPC.closeSettings),
  hidePet: () => ipcRenderer.send(IPC.hidePet),
  quit: () => ipcRenderer.send(IPC.quit),

  menuAction: (action: MenuAction) => ipcRenderer.send(IPC.menuAction, action),
  closeMenu: () => ipcRenderer.send(IPC.closeMenu),

  onState: (listener) => subscribe<PetSnapshot>(IPC.stateChanged, listener),
  onSay: (listener) => subscribe<{ text: string }>(IPC.say, listener),
  onCommand: (listener) =>
    subscribe<{ type: string; [key: string]: unknown }>(IPC.command, listener),
  onDropped: (listener) => subscribe<void>(IPC.dropped, () => listener()),
};

contextBridge.exposeInMainWorld('hajimi', api);
