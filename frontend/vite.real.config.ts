/// <reference types="vitest/config" />
import path from 'node:path';
import type { Socket } from 'node:net';

import react from '@vitejs/plugin-react';
import { defineConfig, type Plugin } from 'vite';

const backendUrl = process.env.POSEIDON_BACKEND_URL ?? 'http://127.0.0.1:5090';
if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(backendUrl)) {
  throw new Error('POSEIDON_BACKEND_URL deve apontar para um Host em loopback HTTP.');
}

const realtimeSockets = new Set<Socket>();

const realtimeDisconnectPlugin = {
  name: 'poseidon-real-e2e-realtime-disconnect',
  configureServer(server) {
    server.middlewares.use(
      '/__e2e__/disconnect-realtime',
      (_request: unknown, response: { statusCode: number; end: (body: string) => void }) => {
        const disconnected = realtimeSockets.size;
        for (const socket of realtimeSockets) socket.destroy();
        response.statusCode = 200;
        response.end(JSON.stringify({ disconnected }));
      },
    );
  },
} satisfies Plugin;

/**
 * Servidor de homologação contra o Host real. Mantém o Vite na raiz do
 * frontend (incluindo Tailwind/PostCSS) e usa same-origin para REST, cookies
 * de sessão e WebSocket/SignalR.
 */
export default defineConfig({
  plugins: [react(), realtimeDisconnectPlugin],
  define: {
    // Perfil de homologação: P1 está publicado e a flag operacional nasce ligada.
    // `VITE_GOVERNANCE_CONTRACT_UI=off` faz rollback imediato para a auditoria legada.
    'import.meta.env.VITE_GOVERNANCE_CONTRACT_UI': JSON.stringify(
      process.env.VITE_GOVERNANCE_CONTRACT_UI ?? 'on',
    ),
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: backendUrl, changeOrigin: false },
      '/hubs': {
        target: backendUrl,
        changeOrigin: false,
        ws: true,
        configure(proxy) {
          proxy.on('proxyReqWs', (_proxyRequest, _request, socket) => {
            realtimeSockets.add(socket);
            socket.once('close', () => realtimeSockets.delete(socket));
          });
          proxy.on('open', (socket) => {
            realtimeSockets.add(socket);
            socket.once('close', () => realtimeSockets.delete(socket));
          });
        },
      },
    },
  },
});
