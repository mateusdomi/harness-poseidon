import { defineConfig, devices } from '@playwright/test';

const packageUrl = process.env.POSEIDON_PACKAGE_URL;
if (!packageUrl || !/^http:\/\/127\.0\.0\.1:\d+$/.test(packageUrl)) {
  throw new Error('POSEIDON_PACKAGE_URL deve apontar para o pacote em loopback.');
}

export default defineConfig({
  testDir: './e2e',
  testMatch: 'package-clean.spec.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  outputDir: 'test-results/package-clean',
  use: {
    ...devices['Desktop Chrome'],
    baseURL: packageUrl,
    serviceWorkers: 'block',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1280, height: 800 },
  },
});
