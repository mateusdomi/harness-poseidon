import path from 'node:path';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const frontendRoot = process.env.HARNESS_FRONTEND_ROOT;
const backendUrl = process.env.HARNESS_BACKEND_URL;
const cacheDirectory = process.env.HARNESS_VITE_CACHE;

if (!frontendRoot || !backendUrl || !cacheDirectory) {
  throw new Error('HARNESS_FRONTEND_ROOT, HARNESS_BACKEND_URL and HARNESS_VITE_CACHE are required.');
}

export default defineConfig({
  root: frontendRoot,
  cacheDir: cacheDirectory,
  plugins: [react()],
  resolve: { alias: { '@': path.join(frontendRoot, 'src') } },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: backendUrl, changeOrigin: false },
      '/hubs': { target: backendUrl, changeOrigin: false, ws: true },
    },
  },
});
