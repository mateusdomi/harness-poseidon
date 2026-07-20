import { defineConfig, devices } from '@playwright/test';

const backendUrl = process.env.POSEIDON_BACKEND_URL ?? 'http://127.0.0.1:5090';
if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(backendUrl)) {
  throw new Error('POSEIDON_BACKEND_URL deve apontar para um Host em loopback HTTP.');
}

export default defineConfig({
  testDir: './e2e',
  testMatch: 'http-real.spec.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  outputDir: 'test-results/http-real',
  use: {
    baseURL: 'http://127.0.0.1:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'desktop-13-dark',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
    },
    {
      name: 'tablet-light',
      use: { ...devices['Desktop Chrome'], viewport: { width: 820, height: 1180 } },
    },
    {
      name: 'mobile-360-dark',
      use: { ...devices['Desktop Chrome'], viewport: { width: 360, height: 800 } },
    },
  ],
  webServer: {
    command: 'npm run dev:real',
    url: 'http://127.0.0.1:5173',
    reuseExistingServer: true,
    timeout: 180_000,
    env: {
      VITE_API_MODE: 'http',
      VITE_API_BASE_URL: '',
      POSEIDON_BACKEND_URL: backendUrl,
    },
  },
});
