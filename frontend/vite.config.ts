/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  build: {
    rollupOptions: {
      output: {
        /**
         * Vendors pesados separados do chunk principal (que passava de 500 kB).
         * Um chunk por família estável de dependência: cacheiam melhor e o
         * código das features (lazy por rota) fica enxuto.
         */
        manualChunks: {
          'vendor-react': ['react', 'react-dom', 'react-router-dom'],
          'vendor-query': ['@tanstack/react-query'],
          'vendor-forms': ['react-hook-form', 'zod'],
          'vendor-i18n': ['i18next', 'react-i18next'],
          'vendor-markdown': ['react-markdown'],
          'vendor-signalr': ['@microsoft/signalr'],
        },
      },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // A suíte completa disputa CPU com builds .NET e múltiplos agentes locais; os testes
    // mantêm suas esperas/assertivas próprias, mas não devem falhar apenas por ultrapassar
    // o default de 5 s sob contenção da máquina.
    testTimeout: 15_000,
    css: false,
    exclude: ['**/node_modules/**', '**/e2e/**'],
  },
});
