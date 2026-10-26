import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

/** 生产环境 preload 路径（dist/main/preload/index.js）。 */
export const PRELOAD_PATH = join(__dirname, '../preload/index.js');

export type RendererPage = 'pet' | 'settings' | 'menu';

export function rendererUrl(page: RendererPage): string {
  const devServer = process.env.HAJIMI_DEV_SERVER_URL;
  if (devServer) return `${devServer.replace(/\/$/, '')}/${page}/index.html`;
  return pathToFileURL(join(__dirname, '../../renderer', page, 'index.html')).href;
}

/** 打包后资源在 app 根目录，开发时在 packages/app 根目录。 */
export function assetPath(file: string): string {
  return join(__dirname, '../../assets', file);
}
