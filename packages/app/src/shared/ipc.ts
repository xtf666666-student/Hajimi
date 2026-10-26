/** IPC 通道名集中定义，避免主进程与渲染进程写错字符串。 */

export const IPC = {
  // 渲染进程 -> 主进程（invoke）
  getSnapshot: 'pet:get-snapshot',
  click: 'pet:click',
  pet: 'pet:pet',
  feed: 'pet:feed',
  updateSettings: 'settings:update',
  resetSettings: 'settings:reset',
  getAppInfo: 'app:info',

  // 渲染进程 -> 主进程（send）
  reportGeometry: 'pet:geometry',
  dragStart: 'pet:drag-start',
  dragEnd: 'pet:drag-end',
  contextMenu: 'pet:context-menu',
  openSettings: 'app:open-settings',
  hidePet: 'app:hide-pet',
  quit: 'app:quit',
  closeSettings: 'settings:close',
  menuAction: 'menu:action',
  closeMenu: 'menu:close',
  ready: 'pet:ready',

  // 主进程 -> 渲染进程（on）
  stateChanged: 'host:state',
  say: 'host:say',
  emotion: 'host:emotion',
  command: 'host:command',
  dropped: 'host:dropped',
  menuInit: 'host:menu-init',
} as const;

export type IpcChannel = (typeof IPC)[keyof typeof IPC];
