import { resolve } from 'node:path';
import { defineConfig } from 'vite';

const root = resolve(__dirname, 'src/renderer');

export default defineConfig({
  root,
  base: './',
  publicDir: resolve(__dirname, 'assets'),
  build: {
    outDir: resolve(__dirname, 'dist/renderer'),
    emptyOutDir: true,
    target: 'chrome128',
    rollupOptions: {
      input: {
        pet: resolve(root, 'pet/index.html'),
        settings: resolve(root, 'settings/index.html'),
        menu: resolve(root, 'menu/index.html'),
      },
    },
  },
  server: {
    port: 5273,
    strictPort: true,
    host: '127.0.0.1',
  },
});
