import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

/** preload 产物（dist/main/preload/index.js） */
export const PRELOAD_PATH = join(__dirname, '../preload/index.js');

export type Page = 'pet' | 'menu';

export function rendererUrl(page: Page): string {
  const devServer = process.env.HAJIMI_DEV_SERVER_URL;
  if (devServer) return `${devServer.replace(/\/$/, '')}/${page}/index.html`;
  return pathToFileURL(join(__dirname, '../../renderer', page, 'index.html')).href;
}

export function assetPath(file: string): string {
  return join(__dirname, '../../assets', file);
}
