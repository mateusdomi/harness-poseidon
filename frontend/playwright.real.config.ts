import { defineConfig, devices } from '@playwright/test';

const backendUrl = process.env.POSEIDON_BACKEND_URL ?? 'http://127.0.0.1:5090';
if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(backendUrl)) {
  throw new Error('POSEIDON_BACKEND_URL deve apontar para um Host em loopback HTTP.');
}
const frontendPort = process.env.POSEIDON_FRONTEND_PORT ?? '5173';
const frontendUrl = process.env.POSEIDON_FRONTEND_URL ?? `http://127.0.0.1:${frontendPort}`;
if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(frontendUrl)) {
  throw new Error('POSEIDON_FRONTEND_URL deve apontar para o Vite em loopback HTTP.');
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
    baseURL: frontendUrl,
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
    command: `npm run dev:real -- --host 127.0.0.1 --port ${frontendPort}`,
    url: frontendUrl,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
    env: {
      VITE_API_MODE: 'http',
      VITE_API_BASE_URL: '',
      VITE_GOVERNANCE_CONTRACT_UI: 'on',
      POSEIDON_BACKEND_URL: backendUrl,
    },
  },
});
