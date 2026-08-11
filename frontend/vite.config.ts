import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, './src'),
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
        manualChunks(id) {
          if (!id.includes('node_modules')) {
            return undefined;
          }

          if (id.includes('/react/') || id.includes('/react-dom/') || id.includes('/react-router-dom/')) {
            return 'vendor-react';
          }

          if (id.includes('/@tanstack/react-query/')) {
            return 'vendor-query';
          }

          if (id.includes('/react-hook-form/') || id.includes('/zod/')) {
            return 'vendor-forms';
          }

          if (id.includes('/i18next/') || id.includes('/react-i18next/')) {
            return 'vendor-i18n';
          }

          if (id.includes('/react-markdown/')) {
            return 'vendor-markdown';
          }

          if (id.includes('/@microsoft/signalr/')) {
            return 'vendor-signalr';
          }

          return undefined;
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
