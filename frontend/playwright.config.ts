import { defineConfig, devices } from '@playwright/test';

const previewPort = process.env.PLAYWRIGHT_PREVIEW_PORT ?? '4173';
const baseURL = process.env.PLAYWRIGHT_BASE_URL ?? `http://localhost:${previewPort}`;

export default defineConfig({
  testDir: './e2e',
  testIgnore: ['**/http-real.spec.ts', '**/package-clean.spec.ts'],
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: 'list',
  // Os padrões do Playwright (30 s por teste, 5 s por expect) foram medidos com a suíte quieta.
  // Com 132 testes em workers paralelos — e, numa máquina de trabalho, o Host e os agentes
  // rodando ao lado — testes corretos passaram a estourar por milissegundos, em specs
  // diferentes a cada rodada. Um gate que muda de cor sem o produto mudar não é um gate: ele
  // ensina a ignorar vermelho. Os tetos abaixo continuam curtos o bastante para pegar
  // travamento de verdade.
  timeout: 60_000,
  expect: { timeout: 15_000 },
  use: {
    baseURL,
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'mobile-360',
      use: { ...devices['Desktop Chrome'], viewport: { width: 360, height: 800 } },
    },
    {
      name: 'desktop-1440',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
    },
  ],
  webServer: {
    command: `npm run build && npm run preview -- --host 127.0.0.1 --port ${previewPort}`,
    url: baseURL,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
  },
});
