import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'node:path';

export default defineConfig({
  base: './',
  plugins: [vue()],
  build: {
    emptyOutDir: true,
    outDir: resolve(__dirname, '../../src/SmartWord.AddIn/Resources/WebClient')
  },
  server: {
    host: '127.0.0.1',
    port: 5173
  }
});
