import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'node:path';
import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs';

const sourceWebClientDir = resolve(__dirname, '../../src/SmartWord.AddIn/Resources/WebClient');

function syncDirectoryIfExists(targetDir) {
  const parentDir = resolve(targetDir, '..');
  mkdirSync(parentDir, { recursive: true });
  rmSync(targetDir, { recursive: true, force: true });
  cpSync(sourceWebClientDir, targetDir, { recursive: true });
}

function syncAddInOutputPlugin() {
  const outputDirectories = [
    resolve(__dirname, '../../src/SmartWord.AddIn/bin/Debug/Resources/WebClient'),
    resolve(__dirname, '../../src/SmartWord.AddIn/bin/Release/Resources/WebClient')
  ];

  return {
    name: 'sync-addin-output',
    closeBundle() {
      outputDirectories
        .filter((directory) => existsSync(resolve(directory, '..', '..')))
        .forEach((directory) => {
          syncDirectoryIfExists(directory);
        });
    }
  };
}

export default defineConfig({
  base: './',
  plugins: [vue(), syncAddInOutputPlugin()],
  build: {
    emptyOutDir: true,
    outDir: sourceWebClientDir
  },
  server: {
    host: '127.0.0.1',
    port: 5173
  }
});
