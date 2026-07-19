import { defineConfig, devices } from '@playwright/test';

// E2E do frontend final executados CONTRA A API REAL (Host servindo o bundle
// embarcado em http://127.0.0.1:5092). Config de propriedade do backend; não
// altera frontend/**. Os specs vivem em frontend/e2e.
export default defineConfig({
  testDir: '../../frontend/e2e',
  fullyParallel: false,
  workers: 1,
  reporter: 'list',
  use: {
    baseURL: 'http://127.0.0.1:5092',
    trace: 'off',
  },
  projects: [
    {
      name: 'desktop-1440-real-api',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
    },
  ],
});
