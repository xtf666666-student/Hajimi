/** IPC 通道名集中定义。 */

export const IPC = {
  // 渲染进程 -> 主进程
  snapshot: 'pet:snapshot',
  refresh: 'pet:refresh',
  click: 'pet:click',
  pet: 'pet:pet',
  openDsh: 'dsh:open',
  openSession: 'dsh:open-session',
  dragStart: 'pet:drag-start',
  dragEnd: 'pet:drag-end',
  geometry: 'pet:geometry',
  listState: 'pet:list-state',
  menu: 'pet:menu',
  menuAction: 'menu:action',
  closeMenu: 'menu:close',
  quit: 'app:quit',

  // 主进程 -> 渲染进程
  state: 'host:state',
  dropped: 'host:dropped',
  command: 'host:command',
} as const;

export type { MenuAction } from './types';
